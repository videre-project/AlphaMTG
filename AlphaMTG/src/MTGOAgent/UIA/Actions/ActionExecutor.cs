/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Controls;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.Core.Reflection;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Interop;
using AlphaMTG.MTGOAgent.UIA;

namespace AlphaMTG.MTGOAgent.UIA.Actions;

/// <summary>
/// Performs actions by driving MTGO's UI the way a person would.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately contains no game logic. It never inspects a target
/// set for legality, never advances a multi-target sequence, and never decides
/// that an action is complete. It locates the control a person would click and
/// clicks it; MTGO's own state machine owns every rule and transition.
/// </para>
/// <para>
/// The one thing it does assert is freshness: the caller must pass the nonce
/// it read, so a click is never delivered against a state the client has
/// already moved past.
/// </para>
/// </remarks>
internal sealed class ActionExecutor(
  Game game,
  AgentState state) : IDisposable
{
  private readonly UIASession _session = new();
  private readonly Game _game = game;
  private readonly CardActionRegistry _registry = game.CardActions;
  private SelectionExecutor Selection =>
    new(_game, _state, _registry, _session);
  private AgentState _state = state;

  public void Dispose() => _session.Dispose();

  public ActionExecution Execute(
    int gameId,
    int nonce,
    int actionId,
    TargetSelection? target,
    int? choiceId,
    int? number,
    IReadOnlyList<int>? order,
    IReadOnlyList<DistributionInput>? distributions,
    string? wish)
  {
    if (state.GameId != gameId)
    {
      throw new ArgumentException(
        $"The state file holds game {state.GameId}, not {gameId}.");
    }
    if (state.Nonce != nonce)
    {
      throw new ArgumentException(
        $"The state file is at nonce {state.Nonce}, not the requested "
          + $"{nonce}. Re-read the state before acting.");
    }

    AgentAction action = state.Actions.SingleOrDefault(candidate =>
      candidate.Id == actionId)
      ?? throw new ArgumentException(
        $"No action with id {actionId} is present in the state file.");

    if (target is not null)
    {
      RequireTargets(action, target);
      return ExecuteTargets(state, action, target);
    }

    if (choiceId is not null)
    {
      RequireChoices(action, choiceId.Value);
      return ExecuteChoice(state, action, choiceId.Value);
    }

    if (number is not null)
    {
      RequireNumber(action, number.Value);
      return ExecuteNumber(action, number.Value);
    }

    if (order is not null)
    {
      RequireOrder(action);
      return ExecuteOrder(action, order);
    }

    if (distributions is not null)
    {
      RequireDistributions(action, distributions);
      return ExecuteDistributions(action, distributions);
    }

    if (wish is not null)
    {
      RequireWish(action);
      return ExecuteWish(action, wish);
    }

    return action.Source.Type switch
    {
      "prompt" => ExecutePromptAction(state, action),
      "card" => ExecuteCardAction(state, action),
      "control" => ExecutePromptControl(state, action),
      _ => throw new InvalidOperationException(
        $"Action {actionId} has unsupported source "
          + $"'{action.Source.Type}'.")
    };
  }

  /// <summary>
  /// Executes a card action natively on the card view-model: the same call
  /// MTGO's own menu entry makes, without opening any menu. Single and
  /// multi-action cards take the same path; the live action is matched by
  /// name, so no UIA is involved.
  /// </summary>
  private ActionExecution ExecuteCardAction(AgentState state, AgentAction action)
  {
    int cardId = action.Source.CardId
      ?? throw new InvalidOperationException(
        $"Card action '{action.Name}' has no source card.");

    CardState card = state.Cards.Single(candidate => candidate.Id == cardId);

    InvokeLiveAction(cardId, action.Name);
    return new ActionExecution(
      action.Id,
      "card-action",
      action.Name,
      cardId,
      $"Executed the action natively ({card.Name}).");
  }

  /// <summary>
  /// Executes a live view-model action by name, the native equivalent of
  /// the single-action click Card_View performs itself.
  /// </summary>
  private void InvokeLiveAction(int cardId, string actionName)
  {
    dynamic? liveCard;
    try
    {
      liveCard = Unbind(_game.GetGameCard(cardId));
    }
    catch (Exception exception)
    {
      throw new InvalidOperationException(
        $"Card {cardId} has no live game object: {exception.Message.Split('\n')[0]}");
    }
    if (liveCard is null)
    {
      throw new InvalidOperationException(
        $"Card {cardId} has no live game object.");
    }

    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic? cardViewModel = null;
      try { cardViewModel = liveCard.Tag?.CardViewModel; }
      catch { }
      if (cardViewModel is null)
      {
        throw new InvalidOperationException(
          $"Card {cardId} has no live card view model.");
      }

      dynamic? match = null;
      foreach (dynamic candidate in (System.Collections.IEnumerable)cardViewModel.Actions)
      {
        string? candidateName = null;
        try { candidateName = candidate.Name; }
        catch { }
        if (string.Equals(candidateName, actionName, StringComparison.Ordinal))
        {
          match = candidate;
          break;
        }
      }
      if (match is null)
      {
        throw new InvalidOperationException(
          $"Live action '{actionName}' is not present on card {cardId}.");
      }

      dynamic cardObject = Unbind(cardViewModel);
      dynamic actionObject = Unbind(match);
      cardObject.ExecuteAction(actionObject);
    }
  }

  /// <summary>
  /// Selects a prompt button by its visible label.
  /// </summary>
  private ActionExecution ExecutePromptAction(AgentState state, AgentAction action) =>
    ExecuteControl(action.Name, action.Id, "prompt-action",
      action.Source.CardId);

  private ActionExecution ExecutePromptControl(AgentState state, AgentAction action)
  {
    ActionSource source = action.Source;
    if (source.ControlId is not PromptControlId controlId
      || source.ClientRevision is not long revision
      || string.IsNullOrWhiteSpace(source.ButtonType))
      throw new ArgumentException("The prompt control has no captured identity.");

    GamePrompt livePrompt = _game.Prompt
      ?? throw new InvalidOperationException("The game has no live prompt.");
    PromptOptionsSnapshot live = livePrompt.CaptureOptions();
    if (live.PromptNonce != state.Nonce
      || live.ClientRevision != revision)
      throw new InvalidOperationException(
        "The prompt controls changed. Re-read the state before acting.");

    PromptControl control = live.Options.Values
      .SelectMany(group => group)
      .OfType<PromptControl>()
      .SingleOrDefault(candidate => candidate.Id == controlId
        && candidate.Name == action.Name
        && candidate.ButtonType == source.ButtonType
        && candidate.Color == source.Color
        && candidate.Enabled)
      ?? throw new InvalidOperationException(
        $"The prompt control '{action.Name}' is no longer available.");
    if (!_registry.ClickPromptButton(control))
      throw new InvalidOperationException(
        $"The prompt control '{action.Name}' changed before it could be clicked.");
    return new ActionExecution(
      action.Id, "prompt-control", action.Name, null,
      "Selected the prompt control.");
  }

  /// <summary>
  /// Selects each requested target through synthesized selection, then the
  /// prompt's own confirm control. Cards resolve natively by id; players
  /// resolve through their player box. MTGO owns the remaining sequence.
  /// </summary>
  private ActionExecution ExecuteTargets(
    AgentState state,
    AgentAction action,
    TargetSelection target)
  {
    TargetSetState set = action.Targets!.Single(candidate =>
      candidate.Index == target.TargetSetId);

    GameObjectState wanted = set.Legal.Single(candidate =>
      candidate.Id == target.TargetId);

    if (wanted.Type == "card")
    {
      Selection.SelectCardNative(wanted.Id);
    }
    else
    {
      Selection.SelectPlayerNative(wanted.Name);
    }

    // MTGO decides whether more targets or a confirmation are required; we
    // only click the confirm control if the client is actually showing one.
    ActionExecution result = new(
      action.Id,
      "target",
      wanted.Name,
      wanted.Id,
      "Selected the target; MTGO owns the remaining sequence.");
    ConfirmPrompt();
    return result;
  }



  /// <summary>
  /// Selects a choice entry exposed by the action.
  /// </summary>
  /// <summary>
  /// Selects a choice through the surface its action type owns. Player
  /// choices click the player box, mana choices click the prompt-box symbol
  /// natively, list choices click the list. Only plain option prompts use
  /// labeled chrome controls.
  /// </summary>
  private ActionExecution ExecuteChoice(
    AgentState state,
    AgentAction action,
    int choiceId)
  {
    ActionChoice choice = action.Choices!.Single(candidate =>
      candidate.Id == choiceId);
    if (action.Type == "ChoosePlayer")
    {
      Selection.SelectPlayerNative(choice.Name);
      return new ActionExecution(
        action.Id,
        "choice",
        choice.Name,
        choiceId,
        "Selected the player.");
    }
    if (action.Type == "PayMana")
    {
      ExecuteManaSymbol(choice.Name);
      return new ActionExecution(
        action.Id,
        "choice",
        choice.Name,
        choiceId,
        "Selected the mana symbol.");
    }
    ExecutePromptButton(choice.Name);
    return new ActionExecution(
      action.Id,
      "choice",
      choice.Name,
      choiceId,
      "Selected the choice.");
  }

  /// <summary>
  /// Clicks a prompt-box symbol button natively by mana color. Symbol
  /// buttons carry no text label, so UIA lookup cannot reach them; the
  /// registry matches the live button and invokes it.
  /// </summary>
  private void ExecuteManaSymbol(string color)
  {
    if (!_registry.ClickPromptButton(null, color))
    {
      throw new InvalidOperationException(
        $"No enabled prompt button for color '{color}'.");
    }
  }

  /// <summary>
  /// Sets a numeric prompt's value natively, then presses the prompt's OK.
  /// </summary>
  private ActionExecution ExecuteNumber(AgentAction action, int number)
  {
    int anchor = AnchorCardId(_state, action.Source.CardId);
    if (anchor == 0)
    {
      throw new InvalidOperationException(
        "No live card available to resolve the duel scene.");
    }
    dynamic? scene = ResolveScene(anchor)
      ?? throw new InvalidOperationException(
        "No duel scene resolved for the numeric prompt.");
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic entry = Unbind(scene).PromptBox.NumberEntry;
      entry.Current = (double)number;
    }
    ExecuteControl("OK", action.Id, "number", action.Source.CardId);
    return new ActionExecution(
      action.Id,
      "number",
      action.Name,
      number,
      $"Set the number to {number}.");
  }

  /// <summary>
  /// Clicks cards in the requested order for an ordering prompt. Each click
  /// is an ordinary selection; MTGO owns the sequence and its completion.
  /// </summary>
  private ActionExecution ExecuteOrder(AgentAction action, IReadOnlyList<int> order)
  {
    foreach (int cardId in order)
      Selection.SelectCard(cardId);
    return new ActionExecution(
      action.Id,
      "order",
      action.Name,
      null,
      $"Ordered {order.Count} cards.");
  }

  /// <summary>
  /// Assigns distributed amounts natively on the live action, then presses
  /// the prompt's OK.
  /// </summary>
  private ActionExecution ExecuteDistributions(
    AgentAction action,
    IReadOnlyList<DistributionInput> distributions)
  {
    dynamic live = LivePromptAction(action);
    using (DiverCommunicator.BeginUIThreadScope())
    {
      foreach (dynamic item in (System.Collections.IEnumerable)live.Distributions)
      {
        int id = Convert.ToInt32(item.Target.Id);
        DistributionInput? want = distributions.FirstOrDefault(
          candidate => candidate.TargetId == id);
        if (want is null) continue;
        item.Value = want.Value;
      }
    }
    ExecuteControl("OK", action.Id, "distribution", action.Source.CardId);
    return new ActionExecution(
      action.Id,
      "distribution",
      action.Name,
      null,
      $"Assigned {distributions.Count} distributions.");
  }

  /// <summary>
  /// Wishes for a named card natively: filters the wish list to the name,
  /// selects the exact match, and presses the wish OK button.
  /// </summary>
  private ActionExecution ExecuteWish(AgentAction action, string wish)
  {
    int anchor = AnchorCardId(_state, action.Source.CardId);
    if (anchor == 0)
    {
      throw new InvalidOperationException(
        "No live card available to resolve the duel scene.");
    }
    dynamic? scene = ResolveScene(anchor)
      ?? throw new InvalidOperationException(
        "No duel scene resolved for the wish dialog.");
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic wishDialog = Unbind(scene).CardWish;
      if (wishDialog is null)
      {
        throw new InvalidOperationException("No wish dialog is open.");
      }
      wishDialog.FilterText = wish;
      dynamic? match = null;
      foreach (dynamic item in
        (System.Collections.IEnumerable)wishDialog.CardSource.View)
      {
        string? name = null;
        try { name = item.Name; }
        catch { }
        if (string.Equals(name, wish, StringComparison.OrdinalIgnoreCase))
        {
          match = item;
          break;
        }
      }
      if (match is null)
      {
        throw new InvalidOperationException(
          $"No wishable card named '{wish}'.");
      }
      wishDialog.SelectedCard = Unbind(match);
    }
    if (!_registry.ClickPromptButton(null, null, "WishCard"))
    {
      throw new InvalidOperationException(
        "No wish confirm button is available.");
    }
    return new ActionExecution(
      action.Id,
      "wish",
      wish,
      null,
      $"Wished for {wish}.");
  }

  /// <summary>
  /// Reads the live prompt action backing a projected action, matched by
  /// type and name. Native sets go through the live object, never the
  /// projection.
  /// </summary>
  private dynamic LivePromptAction(AgentAction action)
  {
    List<GameAction> live =
      ActionProjection.ReadPromptActions(_game.Prompt);
    List<GameAction> matches = live
      .Where(candidate => candidate.Type.ToString() == action.Type
        && candidate.Name == action.Name)
      .ToList();
    if (matches.Count != 1)
    {
      throw new InvalidOperationException(
        $"Expected one live '{action.Name}' ({action.Type}), found "
          + $"{matches.Count}.");
    }
    return matches[0];
  }

  /// <summary>
  /// Clicks a named prompt-box button natively. The button is matched on
  /// the live view model, never the accessibility tree.
  /// </summary>
  public ActionExecution ExecuteControl(
    string label,
    int actionId,
    string operation,
    int? anchorCardId = null)
  {
    ExecutePromptButton(label);
    return new ActionExecution(
      actionId,
      operation,
      label,
      null,
      "Selected the control.");
  }

  /// <summary>
  /// Clicks a named prompt-box button natively, throwing when no enabled
  /// button carries the name. The live button runs the same handler a
  /// click runs; the caller verifies the game moved.
  /// </summary>
  private void ExecutePromptButton(string label)
  {
    if (!_registry.ClickPromptButton(label, null))
    {
      throw new InvalidOperationException(
        $"No enabled prompt button labelled '{label}'.");
    }
  }

  /// <summary>
  /// Clicks a card or player that MTGO has highlighted as selectable.
  /// </summary>
  public ActionExecution Select(SelectionTarget target) =>
    Selection.Select(target);


  /// <summary>
  /// Read-only geometry for a battlefield card. Selection world; no click.
  /// </summary>
  public CardClickPreview PreviewCard(int cardId) =>
    Selection.PreviewCard(cardId);







  /// <summary>
  /// Selects a card in the trigger pop-out by name. Selection world.
  /// </summary>
  public ActionExecution SelectDialogCard(string cardName) =>
    Selection.SelectDialogCard(cardName);

  internal dynamic? ResolveScene(int anchorCardId) =>
    Selection.ResolveScene(anchorCardId);




  private void ConfirmPrompt()
  {
    Thread.Sleep(250);
    _registry.ClickPromptButton("OK", null);
  }

  private int AnchorCardId(AgentState state, int? preferredCardId = null)
  {
    if (preferredCardId.HasValue
      && state.Cards.Any(candidate => candidate.Id == preferredCardId.Value))
    {
      return preferredCardId.Value;
    }

    return state.Cards
      .Where(candidate => candidate.Zone is "Battlefield" or "Hand")
      .Select(candidate => candidate.Id)
      .FirstOrDefault();
  }

  private static void RequireTargets(AgentAction action, TargetSelection target)
  {
    if (action.Targets is null || action.Targets.Count == 0)
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') declares no targets.");
    }
  }

  private static void RequireChoices(AgentAction action, int choiceId)
  {
    if (action.Choices is null || action.Choices.Count == 0)
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') declares no choices.");
    }
  }

  private static void RequireNumber(AgentAction action, int number)
  {
    if (action.Number is null)
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') declares no number.");
    }
    if (number < action.Number.Minimum || number > action.Number.Maximum)
    {
      throw new ArgumentException(
        $"Number {number} is outside [{action.Number.Minimum}, "
          + $"{action.Number.Maximum}] for action {action.Id}.");
    }
  }

  private static void RequireOrder(AgentAction action)
  {
    if (action.Order is null || action.Order.Count == 0)
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') declares no ordering.");
    }
  }

  private static void RequireWish(AgentAction action)
  {
    if (action.Type != "WishCard")
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') is not a wish.");
    }
  }

  private static void RequireDistributions(
    AgentAction action,
    IReadOnlyList<DistributionInput> distributions)
  {
    if (action.Distribution is null || action.Distribution.Count == 0)
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') declares no distribution.");
    }
    foreach (DistributionInput want in distributions)
    {
      DistributionState? target = action.Distribution.FirstOrDefault(
        candidate => candidate.Target.Id == want.TargetId);
      if (target is null)
      {
        throw new ArgumentException(
          $"Target {want.TargetId} is not a distribution target "
            + $"of action {action.Id}.");
      }
      if (want.Value < target.Minimum || want.Value > target.Maximum)
      {
        throw new ArgumentException(
          $"Amount {want.Value} for target {want.TargetId} is outside "
            + $"[{target.Minimum}, {target.Maximum}].");
      }
    }
  }
}
