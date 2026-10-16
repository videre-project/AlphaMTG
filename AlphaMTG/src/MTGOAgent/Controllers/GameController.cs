/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;

using Microsoft.AspNetCore.Mvc;

using MTGOSDK.Core.Diagnostics;
using MTGOSDK.API.Interface.Controls;
using MTGOSDK.API.Interface.ViewModels;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Games.Processors.Events;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Remoting.Interop;

using AlphaMTG.MTGOAgent.Controllers.Models;
using AlphaMTG.MTGOAgent.Controllers.UIA;
using AlphaMTG.MTGOAgent.Router;
using Tracker.Plugin.MTGO;


namespace AlphaMTG.MTGOAgent.Controllers;

/// <summary>
/// Exposes captured SDK state and dispatches requested game interactions.
/// </summary>
[ApiController]
[Route("")]
public sealed class GameController(IMTGOContext session, DecisionLog log) : ControllerBase, IDisposable
{
  // MVC creates a controller for each request. Keep its captured SDK update
  // throughout dispatch, and create accessibility resources only for selection.
  private Game _game = null!;
  private GameStateChangedEventArgs _update = null!;
  private Selection? _selection;

  private DuelSceneViewModel Scene => DuelSceneViewModel.ForGame(_game)
    ?? throw new InvalidOperationException("The game has no loaded duel scene.");
  private Selection Selection => _selection ??= new(_game, _update);

  [NonAction]
  public void Dispose() => _selection?.Dispose();

  //
  // HTTP endpoints
  //

  [HttpGet("status")]
  public IActionResult Status() => Ok(new
  {
    connected = session.Client is not null,
    gameIds = session.Games.Select(game => game.Id)
  });

  private Game ResolveGame(int? gameId)
  {
    if (session.Client is null)
      throw new InvalidOperationException("Tracker has no ready MTGO connection.");
    var games = session.Games;
    return (gameId.HasValue
      ? games.FirstOrDefault(game => game.Id == gameId.Value)
      : games.LastOrDefault())
      ?? throw new InvalidOperationException(gameId.HasValue
        ? $"Tracker is not tracking MTGO game {gameId.Value}."
        : "Tracker is not tracking a live MTGO game.");
  }

  [HttpGet("snapshot")]
  public IActionResult Snapshot([FromQuery] int? gameId = null)
  {
    try
    {
      Game game = ResolveGame(gameId);
      var update = State(game);
      return Ok(GameSnapshot.Create(game.Id, update, GameSnapshot.Actions(update), CanAct(update)));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpGet("preview-card")]
  public IActionResult PreviewCard([FromQuery] int cardId, [FromQuery] int? gameId = null)
  {
    try
    {
      _game = ResolveGame(gameId);
      _update = State(_game);
      GameCard card = CapturedCard(cardId);
      if (card.Zone?.Name != "Battlefield")
        throw new InvalidOperationException($"Card {cardId} is in {card.Zone?.Name}, not the battlefield.");
      var area = Selection.GetClickArea(card);
      return Ok(new
      {
        cardId, area.Target, area.Occluders, area.VisibleAreas,
        area.ClickPoint, area.Positions
      });
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpPost("execute-action")]
  public IActionResult ExecuteAction([FromBody] ExecuteActionRequest request) =>
    Execute(request.GameId, request.Nonce, request, actions =>
    {
      if ((request.TargetId.HasValue && !request.TargetSetId.HasValue)
        || (request.TargetSetId.HasValue && !request.TargetId.HasValue && request.Distributions is null))
        throw new ArgumentException("Supply targetSetId with a targetId or distributions.");
      if (request.ActionId < 0 || request.ActionId >= actions.Count)
        throw new ArgumentException($"No action with id {request.ActionId} is present in this state.");
      var entry = actions[request.ActionId];
      return Dispatch(request, entry.Option, entry.CardId);
    });

  [HttpPost("select-card")]
  public IActionResult SelectCard([FromBody] SelectCardRequest request) =>
    Execute(request.GameId, request.Nonce, request, _ => SelectCard(request.CardId));

  [HttpPost("select-player")]
  public IActionResult SelectPlayer([FromBody] SelectPlayerRequest request) =>
    Execute(request.GameId, request.Nonce, request, _ => SelectPlayer(request.PlayerName));

  //
  // Command dispatch
  //

  private IActionResult Execute(
    int? gameId,
    int nonce,
    object request,
    Func<IReadOnlyList<(IPromptOption Option, int? CardId, ulong? LogicalAddress)>, object> execute)
  {
    using var timing = LocalTelemetry.Begin("alpha.execute", request.GetType().Name,
      gameId: gameId, revision: nonce);
    try
    {
      Game game = _game = ResolveGame(gameId);
      var update = _update = State(game);
      if (!CanAct(update))
        throw new InvalidOperationException("The current SDK prompt does not offer local execution.");

      var actions = GameSnapshot.Actions(update);
      object result;
      try
      {
        if (update.Nonce != nonce)
          throw new ArgumentException($"The SDK update is at nonce {update.Nonce}, not {nonce}.");
        if (State(game).Nonce != nonce)
          throw new InvalidOperationException("The game or local interaction changed. Re-read the state before acting.");
        result = execute(actions);
      }
      catch (Exception exception)
      {
        Record(game, update, actions, request, note: FirstLine(exception));
        throw;
      }

      Record(game, update, actions, request, result);
      return Ok(result);
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  //
  // Action parameters and prompt controls
  //

  private object Dispatch(ExecuteActionRequest request, IPromptOption option, int? cardId)
  {
    int actionId = request.ActionId;
    int payloadCount = (request.TargetId.HasValue ? 1 : 0)
      + (request.ChoiceId.HasValue ? 1 : 0)
      + (request.Number.HasValue ? 1 : 0)
      + (request.Order is not null ? 1 : 0)
      + (request.Distributions is not null ? 1 : 0)
      + (request.Wish is not null ? 1 : 0);
    if (payloadCount > 1)
      throw new ArgumentException("Supply only one action payload per request.");

    if (request.TargetId.HasValue)
    {
      if (option is not CardAction card || card.Targets.Count == 0)
        throw new ArgumentException($"Action {actionId} ('{option.Name}') declares no targets.");
      return ExecuteTargets(actionId, card, request.TargetSetId!.Value, request.TargetId!.Value);
    }

    if (request.ChoiceId is int choiceId)
      return ExecuteChoice(actionId, option, choiceId);

    if (request.Number is int number)
    {
      if (option is not NumericAction numeric)
        throw new ArgumentException($"Action {actionId} ('{option.Name}') declares no number.");
      return ExecuteNumber(actionId, numeric, number);
    }

    if (request.Order is { } order)
    {
      if (option is not OrderingAction ordering || ordering.OrderedTargets.Count == 0)
        throw new ArgumentException($"Action {actionId} ('{option.Name}') declares no ordering.");
      return ExecuteOrder(actionId, ordering, order);
    }

    if (request.Distributions is { } distributions)
    {
      return ExecuteDistributions(actionId, option, request.TargetSetId, distributions);
    }

    if (request.Wish is { } wish)
    {
      if (option is not GameAction { Type: ActionType.WishCard } action)
        throw new ArgumentException($"Action {actionId} ('{option.Name}') is not a wish.");
      return ExecuteWish(actionId, action, wish);
    }

    return option switch
    {
      CardAction card => ExecuteCardAction(actionId, card, cardId!.Value),
      PromptControl control => ExecutePromptControl(actionId, control),
      ManaPoolAction or SelectFromListAction or CardSelectorAction =>
        throw new ArgumentException($"Action {actionId} requires a choiceId."),
      GameAction action => ExecutePromptAction(actionId, action),
      _ => throw new NotSupportedException($"Unsupported SDK prompt option: {option.GetType().Name}.")
    };
  }

  /// <summary>
  /// Executes the uniquely matching action on the source card's MTGO view model.
  /// </summary>
  private object ExecuteCardAction(int actionId, CardAction action, int cardId)
  {
    GameCard card = CapturedCard(cardId);

    InvokeLiveAction(cardId, action.Name);
    return Result(actionId, "card-action", action.Name, cardId,
      $"Executed the MTGO action ({card.Name}).");
  }

  /// <summary>
  /// Resolves the captured action name against the card's current MTGO actions.
  /// </summary>
  private void InvokeLiveAction(int cardId, string actionName)
  {
    GameCard card = _game.GetGameCard(cardId)
      ?? throw new InvalidOperationException($"Card {cardId} has no live game object.");
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic cardViewModel = Unbind(card).Tag.CardViewModel;
      if (cardViewModel is null)
        throw new InvalidOperationException($"Card {cardId} has no live card view model.");

      var matches = card.Actions
        .Where(candidate => string.Equals(
          candidate.Name, actionName, StringComparison.Ordinal))
        .ToList();
      if (matches.Count != 1)
        throw new InvalidOperationException(
          $"Expected one live '{actionName}' action on card {cardId}, found {matches.Count}.");
      cardViewModel.ExecuteAction(Unbind(matches[0]));
    }
  }

  private object ExecutePromptControl(int actionId, PromptControl control)
  {
    if (!ClickPromptButton(control))
      throw new InvalidOperationException(
        $"The prompt control '{control.Name}' changed before it could be clicked.");
    return Result(
      actionId, "prompt-control", control.Name, null,
      "Selected the prompt control.");
  }

  /// <summary>
  /// Clicks the requested target through synthesized selection. Cards resolve
  /// by ID, and players resolve through their player box.
  /// </summary>
  private object ExecuteTargets(int actionId, CardAction action, int targetSetId, int targetId)
  {
    if (targetSetId < 0 || targetSetId >= action.Targets.Count)
      throw new ArgumentException($"Action {actionId} has no target set {targetSetId}.");

    Targetable wanted = action.Targets[targetSetId].LegalTargets.Single(candidate =>
      candidate.Id == targetId);
    string name;
    switch ((object)wanted.ToGameObject())
    {
      case GameCard card:
        Selection.SelectCard(CapturedCard(card.Id));
        name = card.Name;
        break;
      case GamePlayer player:
        Selection.SelectPlayer(player.Name);
        name = player.Name;
        break;
      default:
        throw new InvalidOperationException($"Target {wanted.Id} is not a card or player.");
    }
    return Result(actionId, "target", name, wanted.Id, "Clicked the target.");
  }

  /// <summary>
  /// Selects an SDK choice using its player box or visible prompt button.
  /// </summary>
  private object ExecuteChoice(int actionId, IPromptOption option, int choiceId)
  {
    string name;
    switch (option)
    {
      case ManaPoolAction mana:
        MagicColors color = (MagicColors)choiceId;
        if (!GameSnapshot.ManaChoices(mana).Contains(color))
          throw new ArgumentException($"Color choice {choiceId} is not available for action {actionId}.");
        name = color.ToString();
        var liveMana = (ManaPoolAction)LivePromptAction(mana);
        GameActionExecution.SelectColor(liveMana, color);
        GameActionExecution.Execute(_game, liveMana);
        break;
      case SelectPlayerAction players:
        name = players.AvailablePlayers.Single(player => player.UserId == choiceId).Name;
        Selection.SelectPlayer(name);
        break;
      case SelectFromListAction list:
        name = list.AvailableItems.Single(item => item.Value == choiceId).Name;
        var liveList = (SelectFromListAction)LivePromptAction(list);
        GameActionExecution.SelectItem(liveList, choiceId);
        GameActionExecution.Execute(_game, liveList);
        break;
      case CardSelectorAction cards:
        var choice = cards.Choices.Single(item => item.CTN == choiceId && item.CanChoose);
        name = $"Card {choice.CTN}";
        var liveCards = (CardSelectorAction)LivePromptAction(cards);
        GameActionExecution.SelectCard(liveCards, choice.CTN);
        GameActionExecution.Execute(_game, liveCards);
        break;
      default:
        throw new ArgumentException($"Action {actionId} ('{option.Name}') declares no choices.");
    }
    return Result(actionId, "choice", name, choiceId, "Selected the choice.");
  }

  /// <summary>
  /// Sets a numeric prompt's value, then presses the prompt's OK.
  /// </summary>
  private object ExecuteNumber(int actionId, NumericAction action, int number)
  {
    if (number < action.Minimum || number > action.Maximum)
      throw new ArgumentException(
        $"Number {number} is outside [{action.Minimum}, {action.Maximum}] for action {actionId}.");

    dynamic scene = Unbind(Scene);
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic entry = scene.PromptBox.NumberEntry;
      entry.Current = (double)number;
    }
    ExecutePromptButton("OK");
    return Result(actionId, "number", action.Name, number,
      $"Set the number to {number}.");
  }

  /// <summary>
  /// Sets and submits the requested combat order through MTGO's model.
  /// </summary>
  private object ExecuteOrder(int actionId, OrderingAction action, IReadOnlyList<int> order)
  {
    var live = (OrderingAction)LivePromptAction(action);
    GameActionExecution.SetOrder(live, order);
    GameActionExecution.Execute(_game, live);
    return Result(actionId, "order", action.Name, null,
      $"Ordered {order.Count} cards.");
  }

  /// <summary>
  /// Assigns and submits combat damage, or prepares amounts on a card's target set.
  /// </summary>
  private object ExecuteDistributions(int actionId, IPromptOption option,
    int? targetSetId, IReadOnlyList<DistributionInput> distributions)
  {
    // ToDictionary rejects duplicate target IDs before any MTGO state changes.
    var amounts = distributions.ToDictionary(item => item.TargetId, item => item.Value);
    switch (option)
    {
      case CombatDamageAssignmentAction damage when targetSetId is null:
        var liveDamage = (CombatDamageAssignmentAction)LivePromptAction(damage);
        GameActionExecution.SetDistribution(liveDamage, amounts);
        GameActionExecution.Execute(_game, liveDamage);
        break;
      case CardAction card when targetSetId is int index:
        if (index < 0 || index >= card.Targets.Count || !card.Targets[index].IsDistributedAmongTargets)
          throw new ArgumentException($"Action {actionId} has no distribution target set {index}.");
        CardAction liveCard = LiveCardAction(card);
        GameActionExecution.SetDistribution(liveCard.Targets[index], amounts);
        if (!liveCard.IsTargetsSet)
          return Result(actionId, "distribution", card.Name, index,
            "Assigned the amounts; additional target sets still need selections.");
        GameActionExecution.Execute(_game, liveCard);
        break;
      default:
        throw new ArgumentException(
          $"Action {actionId} requires combat distributions or a distribution targetSetId.");
    }
    return Result(actionId, "distribution", option.Name, null,
      $"Assigned {amounts.Count} distributions.");
  }

  /// <summary>
  /// Resolves a captured card action against its source card's live actions.
  /// </summary>
  private CardAction LiveCardAction(CardAction action)
  {
    GameCard card = _game.GetGameCard(action.Card.Id)
      ?? throw new InvalidOperationException($"Card {action.Card.Id} has no live game object.");
    return card.Actions.Single(candidate => candidate.ActionId == action.ActionId
      && candidate.Name == action.Name);
  }

  /// <summary>
  /// Wishes for a named card: filters the wish list to the name,
  /// selects the exact match, and presses the wish OK button.
  /// </summary>
  private object ExecuteWish(int actionId, GameAction action, string wish)
  {
    dynamic scene = Unbind(Scene);
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic wishDialog = scene.CardWish;
      if (wishDialog is null)
      {
        throw new InvalidOperationException("No wish dialog is open.");
      }
      wishDialog.FilterText = wish;
      dynamic? match = null;
      foreach (dynamic item in
        (System.Collections.IEnumerable)wishDialog.CardSource.View)
      {
        string name = item.Name;
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
    if (!ClickPromptButton(null, null, "WishCard"))
    {
      throw new InvalidOperationException(
        "No wish confirm button is available.");
    }
    return Result(actionId, "wish", wish, null, $"Wished for {wish}.");
  }

  /// <summary>
  /// Resolves the submitted action against the current MTGO prompt by type and name.
  /// </summary>
  private GameAction LivePromptAction(GameAction action)
  {
    GamePrompt prompt = _game.Prompt
      ?? throw new InvalidOperationException("The game has no live prompt.");
    List<GameAction> matches = prompt.GameActions.Values.SelectMany(group => group)
      .Where(candidate => candidate.Type == action.Type
        && candidate.ActionId == action.ActionId && candidate.Name == action.Name)
      .ToList();
    if (matches.Count != 1)
    {
      throw new InvalidOperationException(
        $"Expected one live '{action.Name}' ({action.Type}), found "
          + $"{matches.Count}.");
    }
    return matches[0];
  }

  private object ExecutePromptAction(int actionId, GameAction action)
  {
    if (action.Type == ActionType.ChoosePile)
      GameActionExecution.Execute(_game, LivePromptAction(action));
    else
      ExecutePromptButton(action.Name);
    return Result(actionId, "prompt-action", action.Name, null, "Executed the prompt action.");
  }

  /// <summary>
  /// Clicks a named prompt-box button, throwing when no enabled
  /// button carries the name.
  /// </summary>
  private void ExecutePromptButton(string label)
  {
    if (!ClickPromptButton(label, null))
    {
      throw new InvalidOperationException(
        $"No enabled prompt button labelled '{label}'.");
    }
  }

  /// <summary>
  /// Clicks a prompt-box option button natively by name or mana color.
  /// </summary>
  /// <returns>True when a matching enabled button was clicked.</returns>
  private bool ClickPromptButton(
    string? name, string? color, string? typeContains = null)
  {
    dynamic scene = Unbind(Scene);

    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic? promptBox = Try(() => scene.PromptBox);
      if (promptBox is null) return false;

      dynamic? match = FindPromptButton(
          Try(() => promptBox.StandardButtons),
          name, null, typeContains)
        ?? FindPromptButton(
          Try(() => promptBox.ManaButtons),
          name, color, typeContains);
      if (match is null) return false;

      return Try<bool>(() =>
      {
        match.OnClick();
        return true;
      });
    }
  }

  /// <summary>
  /// Clicks the live button identified by a captured prompt control.
  /// </summary>
  /// <remarks>
  /// Checks the collection index, type, label, color, and availability before
  /// clicking. A button with a backing game action must be executed through that
  /// action. Each remote call runs on the UI thread, but validation and clicking
  /// are separate calls.
  /// </remarks>
  private bool ClickPromptButton(PromptControl control)
  {
    ArgumentNullException.ThrowIfNull(control);
    if (control.Id.Index < 0) return false;

    dynamic scene = Unbind(Scene);
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic? promptBox = Try(() => scene.PromptBox);
      if (promptBox is null) return false;

      dynamic? collection = control.Id.Group switch
      {
        PromptButtonGroup.Standard => Try(() => promptBox.StandardButtons),
        PromptButtonGroup.Mana     => Try(() => promptBox.ManaButtons),
        _                          => null
      };
      if (collection is null) return false;

      int index = 0;
      foreach (dynamic button in (System.Collections.IEnumerable)collection)
      {
        if (index++ != control.Id.Index) continue;
        if (!Try<bool>(() => (bool)button.Visible) || !Try<bool>(() => (bool)button.Enabled))
          return false;

        // An unreadable Action property cannot establish that this is a local
        // control. Reject it to avoid duplicating GamePrompt.GameActions.
        if (Try(() => button.Action is not null, true))
          return false;

        string? buttonType = Try(() => (string)button.GetType().Name);
        string? buttonColor = Try(() => (string)button.Color?.ToString()!);
        string? buttonName = Try(() => (string)button.Name) ?? buttonColor;
        if (!string.Equals(buttonType, control.ButtonType, StringComparison.Ordinal)
          || !string.Equals(buttonName, control.Name, StringComparison.Ordinal)
          || !string.Equals(buttonColor, control.Color, StringComparison.Ordinal))
          return false;

        return Try<bool>(() =>
        {
          button.OnClick();
          return true;
        });
      }
      return false;
    }
  }

  private static dynamic? FindPromptButton(
    dynamic? buttons, string? name, string? color, string? typeContains)
  {
    if (buttons is null) return null;
    if (name is null && color is null && typeContains is null) return null;

    foreach (dynamic button in (System.Collections.IEnumerable)buttons)
    {
      if (!Try<bool>(() => (bool)button.Visible)) continue;
      if (!Try<bool>(() => (bool)button.Enabled)) continue;
      if (name is not null)
      {
        string? buttonName = Try(() => (string)button.Name);
        if (!string.Equals(buttonName, name, StringComparison.OrdinalIgnoreCase))
          continue;
      }
      if (color is not null)
      {
        string? buttonColor = Try(() => (string)button.Color.ToString());
        if (!string.Equals(buttonColor, color, StringComparison.OrdinalIgnoreCase))
          continue;
      }
      if (typeContains is not null)
      {
        string? typeName = Try(() => (string)button.GetType().Name);
        if (typeName is null
          || !typeName.Contains(typeContains, StringComparison.OrdinalIgnoreCase))
          continue;
      }
      return button;
    }

    return null;
  }

  //
  // Card and player selections
  //

  private object SelectCard(int cardId)
  {
    var selectable = _update.Interaction.Cards.Values
      .SingleOrDefault(card => card.CardId == cardId)?.Selection
      ?? throw new ArgumentException($"Card {cardId} is absent from this SDK interaction capture.");
    if (!selectable.IsSelectable)
      throw new InvalidOperationException($"Card {cardId} is not selectable right now.");

    GameCard card = CapturedCard(cardId);
    Point click = Selection.SelectCard(card);
    return Result(0, "select-card", card.Name, cardId,
      $"Clicked the card at {click.X},{click.Y}.");
  }

  private object SelectPlayer(string playerName)
  {
    var player = _update.Snapshot.Players.Single(entry => entry.Value.Name == playerName);
    if (!_update.Interaction.Players.Any(candidate =>
      candidate.PlayerIndex == player.Key && candidate.IsTargetable))
      throw new InvalidOperationException($"Player '{playerName}' is not targetable in this SDK update.");

    Selection.SelectPlayer(playerName);
    return Result(0, "select-player", playerName, null, "Selected the player.");
  }

  private GameCard CapturedCard(int cardId) =>
    _update.Snapshot.Cards.GetValueOrDefault(cardId)
      ?? _update.Snapshot.HiddenCards.GetValueOrDefault(cardId)
      ?? throw new ArgumentException($"Card {cardId} is absent from this SDK gameplay snapshot.");

  private static object Result(int actionId, string operation, string name, int? value, string detail) =>
    new { actionId, operation, name, value, detail };

  /// <summary>
  /// Builds the diagnostic response from the update used for execution.
  /// A logging failure must not turn a dispatched action into a failed HTTP request.
  /// </summary>
  private void Record(
    Game game,
    GameStateChangedEventArgs update,
    IReadOnlyList<(IPromptOption Option, int? CardId, ulong? LogicalAddress)> actions,
    object request,
    object? result = null,
    string? note = null)
  {
    try
    {
      log.Record(game.Id, GameSnapshot.Create(game.Id, update, actions, CanAct(update)),
        State(game).Nonce, request, result, note);
    }
    catch
    {
      // Response formatting can also fail before the log writer receives it.
    }
  }

  private static GameStateChangedEventArgs State(Game game) =>
    GameProcessor.ForGame(game).GetProcessor<PromptProcessor>()?.LastState
      ?? throw new InvalidOperationException($"No captured SDK update for game {game.Id} yet.");

  private bool CanAct(GameStateChangedEventArgs update) =>
    update.Snapshot.Players.TryGetValue(update.Prompt.PromptedPlayer, out var player)
      && player.UserId == session.UserId
      && (update.Snapshot.Players.Count == 1 || player.HasPriority);

  private static string FirstLine(Exception exception) => exception.Message.Split('\n')[0];
}
