/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace AlphaMTG.MTGOAgent.UIA;

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
internal sealed class ActionExecutor(string statePath) : IDisposable
{
  private readonly UiaSession _session = new();
  private AgentState _state = null!;

  public void Dispose() => _session.Dispose();

  public ActionExecution Execute(
    int gameId,
    int nonce,
    int actionId,
    TargetSelection? target,
    int? choiceId)
  {
    AgentState state = AgentStateStore.Read(statePath);
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
      return ClickTargets(state, action, target);
    }

    if (choiceId is not null)
    {
      RequireChoices(action, choiceId.Value);
      return ClickChoice(state, action, choiceId.Value);
    }

    return action.Source.Type switch
    {
      "prompt" => ClickPromptAction(action),
      "card" => ClickCardAction(state, action),
      _ => throw new InvalidOperationException(
        $"Action {actionId} has unsupported source "
          + $"'{action.Source.Type}'.")
    };
  }

  /// <summary>
  /// Activates a card menu entry, then clicks the requested target if the
  /// action opened MTGO's targeting interaction.
  /// </summary>
  private ActionExecution ClickCardAction(AgentState state, AgentAction action)
  {
    int cardId = action.Source.CardId
      ?? throw new InvalidOperationException(
        $"Card action '{action.Name}' has no source card.");

    CardState card = state.Cards.Single(candidate => candidate.Id == cardId);
    AutomationElement? element = _session.FindCardElement(
      card.Zone,
      card.Name,
      card.NameOrdinal ?? 0)
      ?? throw new InvalidOperationException(
        $"No visible {card.Zone} element for {card.Name} "
          + $"(card {card.Id}).");

    DismissStaleMenu();
    element.RightClick(moveMouse: true);

    AutomationElement? menu = _session.WaitForContextMenu(
      TimeSpan.FromSeconds(2))
      ?? throw new InvalidOperationException(
        "MTGO did not open a card menu after the right click.");

    try
    {
      AutomationElement item = FindMenuItem(menu, action.Name);
      item.Click(moveMouse: true);
    }
    finally
    {
      DismissStaleMenu();
    }

    return new ActionExecution(
      action.Id,
      "card-action",
      action.Name,
      cardId,
      "Clicked the card menu entry.");
  }

  /// <summary>
  /// Clicks a prompt button by its visible label.
  /// </summary>
  private ActionExecution ClickPromptAction(AgentAction action) =>
    ClickControl(action.Name, action.Id, "prompt-action");

  /// <summary>
  /// Clicks each requested target, then the prompt's own confirm control.
  /// </summary>
  private ActionExecution ClickTargets(
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
      CardState card = state.Cards.Single(candidate =>
        candidate.Id == wanted.Id);
      AutomationElement? element = _session.FindCardElement(
        card.Zone,
        card.Name,
        card.NameOrdinal ?? 0)
        ?? throw new InvalidOperationException(
          $"No visible element for target {card.Name} (card {card.Id}).");
      element.Click(moveMouse: true);
    }
    else
    {
      AutomationElement? player = _session.FindControlByLabel(wanted.Name)
        ?? throw new InvalidOperationException(
          $"No visible control for target {wanted.Name}.");
      player.Click(moveMouse: true);
    }

    // MTGO decides whether more targets or a confirmation are required; we
    // only click the confirm control if the client is actually showing one.
    ActionExecution result = new(
      action.Id,
      "target",
      wanted.Name,
      wanted.Id,
      "Clicked the target; MTGO owns the remaining sequence.");
    ConfirmIfPrompted();
    return result;
  }

  /// <summary>
  /// Clicks a choice entry exposed by the action.
  /// </summary>
  private ActionExecution ClickChoice(
    AgentState state,
    AgentAction action,
    int choiceId)
  {
    ActionChoice choice = action.Choices!.Single(candidate =>
      candidate.Id == choiceId);
    AutomationElement? element = _session.FindControlByLabel(choice.Name)
      ?? throw new InvalidOperationException(
        $"No visible control for choice '{choice.Name}'.");

    element.Click(moveMouse: true);
    return new ActionExecution(
      action.Id,
      "choice",
      choice.Name,
      choiceId,
      "Clicked the choice.");
  }

  /// <summary>
  /// Clicks a card or player that MTGO has highlighted as selectable.
  /// </summary>
  /// <remarks>
  /// Cards resolve by their <c>Image-&lt;name&gt;</c> automation id and players
  /// by their name inside the duel scene, so the click lands on the intended
  /// object rather than on whatever happens to occupy those coordinates. The
  /// executor fails if the element is absent or disabled instead of clicking
  /// blindly, because a mis-click during a turn is worse than an error.
  /// </remarks>
  public ActionExecution Select(SelectionTarget target)
  {
    _state = AgentStateStore.Read(statePath);

    AutomationElement element;
    string operation;
    string name;

    if (target.CardId is int cardId)
    {
      CardState card = _state.Cards
        .SingleOrDefault(candidate => candidate.Id == cardId)
        ?? throw new ArgumentException(
          $"No card {cardId} is present in the state file.");

      element = _session.FindCardElement(
        card.Zone,
        card.Name,
        card.NameOrdinal ?? 0)
        ?? throw new InvalidOperationException(
          $"No visible element for card {cardId} ({card.Name}).");

      name = card.Name;
      operation = "select-card";
    }
    else if (!string.IsNullOrEmpty(target.PlayerName))
    {
      element = _session.FindPlayerBox(target.PlayerName)
        ?? throw new InvalidOperationException(
          $"No player box for '{target.PlayerName}' in the duel scene.");

      name = target.PlayerName;
      operation = "select-player";
    }
    else
    {
      throw new ArgumentException(
        "A selection needs either a card id or a player name.");
    }

    DismissStaleMenu();

    if (!UiaSession.GetIsEnabled(element))
    {
      throw new InvalidOperationException(
        $"'{name}' is present but not enabled; it is not selectable now.");
    }

    element.Click(moveMouse: true);

    return new ActionExecution(
      0,
      operation,
      name,
      target.CardId,
      "Clicked the selected object.");
  }

  /// <summary>
  /// Clicks an enabled prompt confirm control when MTGO is showing one.
  /// </summary>
  public ActionExecution ClickControl(
    string label,
    int actionId,
    string operation)
  {
    AutomationElement? element = _session.FindControlByLabel(label)
      ?? throw new InvalidOperationException(
        $"No visible enabled control labelled '{label}'.");

    element.Click(moveMouse: true);
    return new ActionExecution(
      actionId,
      operation,
      label,
      null,
      "Clicked the control.");
  }

  private void ConfirmIfPrompted()
  {
    Thread.Sleep(250);
    AutomationElement? confirm = _session.FindControlByLabel("OK");
    confirm?.Click(moveMouse: true);
  }

  private static AutomationElement FindMenuItem(AutomationElement menu, string name)
  {
    // Scope to real menu items: a context menu container also exposes the
    // card it belongs to, and the popup chrome, as descendants.
    AutomationElement[] entries = menu
      .FindAllDescendants()
      .Where(UiaSession.IsVisible)
      .Where(element => UiaSession.GetControlType(element)
        == ControlType.MenuItem)
      .ToArray();

    AutomationElement[] items = entries
      .Where(UiaSession.GetIsEnabled)
      .Where(element => UiaSession.LabelsMatch(UiaSession.GetName(element), name))
      .ToArray();

    if (items.Length == 1) return items[0];

    string available = string.Join(", ", entries.Select(element =>
      $"'{UiaSession.GetName(element) ?? "<unnamed>"}'"
        + (UiaSession.GetIsEnabled(element) ? "" : " (disabled)")));

    return items.Length == 0
      ? throw new InvalidOperationException(
        $"No enabled menu entry labelled '{name}' was open. Entries: "
          + available)
      : throw new InvalidOperationException(
        $"'{name}' matched {items.Length} menu entries. Entries: {available}");
  }

  private void DismissStaleMenu()
  {
    if (!_session.HasContextMenu()) return;

    _session.DismissContextMenu();
    if (_session.HasContextMenu())
    {
      throw new InvalidOperationException(
        "A card menu was already open and could not be dismissed.");
    }
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
}
