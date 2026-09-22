/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using MTGOSDK.API.Play;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Games.Processors.EventArgs;


namespace AlphaMTG.MTGOAgent;

internal sealed class GameStateExporter(ILogger logger, int localUserId) : IDisposable
{
  private readonly ConcurrentDictionary<int, Game> _games = new();
  private readonly ConcurrentDictionary<int, GamePrompt> _prompts = new();
  private readonly object _writeLock = new();
  private readonly string _outputPath = AgentStateStore.DefaultPath;

  public void Start()
  {
    GameProcessor.OnNewGame += AttachGame;
    GameProcessor.EnsureHookInitialized();
    foreach (var match in EventManager.JoinedEvents.OfType<Match>())
    {
      if (match.CurrentGame is { } game)
        AttachGame(game);
    }
    logger.LogInformation("MTGO game-state exporter is ready.");
  }

  private void AttachGame(Game game)
  {
    if (!_games.TryAdd(game.Id, game))
      return;

    game.OnPromptChanged += args =>
    {
      if (args.Nonce != args.Snapshot.Nonce || args.Nonce != args.Prompt.Nonce)
        return;

      _prompts[game.Id] = args.Prompt;
      Export(game, args.Snapshot, args.Prompt);
    };
    game.OnCardChanged += args => ExportCurrent(game, args);
    game.OnPlayerChanged += args => ExportCurrent(game, args);
    game.OnZoneChanged += args => ExportCurrent(game, args);
    game.ReadyProcessor();
    logger.LogInformation("Tracking MTGO game {GameId}.", game.Id);
  }

  private void ExportCurrent(Game game, GameEventArgs args)
  {
    if (_prompts.TryGetValue(game.Id, out var prompt)
        && prompt.Nonce == args.Nonce)
    {
      Export(game, args.Snapshot, prompt);
    }
  }

  private void Export(Game game, GameStateSnapshot snapshot, GamePrompt prompt)
  {
    try
    {
      if (snapshot.Nonce != prompt.Nonce)
        return;

      if (!snapshot.Players.TryGetValue(snapshot.PromptedPlayer, out var player)
          || player.UserId != localUserId)
      {
        return;
      }

      // Solitaire does not consistently mark its sole player as having priority.
      if (snapshot.Players.Count > 1 && !player.HasPriority)
        return;

      var document = BuildDocument(game, snapshot, prompt);
      lock (_writeLock)
        AgentStateStore.Write(_outputPath, document);

      logger.LogInformation(
        "Wrote game {GameId} state {Nonce} with {ActionCount} actions.",
        document.GameId,
        document.Nonce,
        document.Actions.Count
      );
    }
    catch (Exception exception)
    {
      logger.LogError(exception, "Failed to export MTGO game {GameId}.", game.Id);
    }
  }

  private static AgentState BuildDocument(
    Game game,
    GameStateSnapshot snapshot,
    GamePrompt prompt
  )
  {
    var snapshotCards = snapshot.Cards.Values
      .OrderBy(card => Read(() => card.Zone?.Name) ?? string.Empty)
      .ThenBy(card => Read(() => card.OwnerIndex))
      .ThenBy(card => card.Id)
      .ToList();
    var zonePositions = BuildZonePositions(game, snapshotCards);
    var cards = snapshotCards
      .Select(card => ProjectCard(card, zonePositions.GetValueOrDefault(card.Id)))
      .OrderBy(card => card.Zone ?? string.Empty)
      .ThenBy(card => card.OwnerIndex)
      .ThenBy(card => card.ZoneIndex ?? int.MaxValue)
      .ThenBy(card => card.Id)
      .ToList();

    var candidates = new List<ActionCandidate>();
    foreach (var action in ReadPromptActions(prompt))
    {
      var source = action is CardAction cardAction
        ? new ActionSource("card", Read(() => cardAction.Card.Id))
        : new ActionSource("prompt");
      candidates.Add(new ActionCandidate(action, source));
    }

    foreach (var card in cards)
    {
      var liveCard = Read(() => game.GetGameCard(card.Id));
      foreach (var action in Read(() => liveCard?.Actions.ToList()) ?? [])
        candidates.Add(new ActionCandidate(action, new ActionSource("card", card.Id)));
    }

    var actions = candidates
      .Select((candidate, index) => ProjectAction(index, candidate))
      .ToList();
    var players = snapshot.Players
      .OrderBy(entry => entry.Key)
      .Select(entry => new PlayerState(
        entry.Key,
        Read(() => entry.Value.UserId),
        Read(() => entry.Value.Name) ?? string.Empty,
        Read(() => entry.Value.Life),
        Read(() => entry.Value.HasPriority)
      ))
      .ToList();

    return new AgentState(
      DateTimeOffset.UtcNow,
      game.Id,
      snapshot.Nonce,
      snapshot.Timestamp,
      snapshot.TurnNumber,
      snapshot.CurrentPhase.ToString(),
      new PromptState(prompt.Text, prompt.PromptedPlayer),
      players,
      cards,
      actions
    );
  }

  private static CardState ProjectCard(GameCard card, ZonePosition? position)
  {
    return new CardState(
      card.Id,
      card.SourceId,
      card.CTN,
      card.Name,
      Read(() => card.Zone?.Name),
      Read(() => card.OwnerIndex),
      Read(() => card.ControllerIndex),
      position?.Index,
      position?.NameOrdinal
    );
  }

  private static AgentAction ProjectAction(int id, ActionCandidate candidate)
  {
    var action = candidate.Action;
    IReadOnlyList<TargetSetState>? targets = null;
    NumberRange? number = null;
    IReadOnlyList<ActionChoice>? choices = null;
    IReadOnlyList<int>? order = null;
    IReadOnlyList<DistributionState>? distribution = null;

    if (action is CardAction cardAction)
    {
      targets = (Read(() => cardAction.Targets.ToList()) ?? [])
        .Select((targetSet, index) => ProjectTargetSet(index, targetSet))
        .ToList();
      if (targets.Count == 0)
        targets = null;
    }

    switch (action)
    {
      case NumericAction numeric:
        number = new NumberRange(numeric.Minimum, numeric.Maximum, numeric.Initial);
        break;
      case SelectPlayerAction selectPlayer:
        choices = (Read(() => selectPlayer.AvailablePlayers.ToList()) ?? [])
          .Select(player => new ActionChoice(player.UserId, player.Name))
          .ToList();
        break;
      case SelectFromListAction selectFromList:
        choices = (Read(() => selectFromList.AvailableItems.ToList()) ?? [])
          .Select(item => new ActionChoice(item.Value, item.Name))
          .ToList();
        break;
      case CardSelectorAction cardSelector:
        choices = (Read(() => cardSelector.Choices) ?? [])
          .Where(choice => choice.CanChoose)
          .Select(choice => new ActionChoice(choice.CTN, $"Card {choice.CTN}"))
          .ToList();
        break;
      case OrderingAction ordering:
        order = (Read(() => ordering.OrderedTargets.ToList()) ?? [])
          .Select(card => card.Id)
          .ToList();
        break;
      case CombatDamageAssignmentAction damage:
        distribution = (Read(() => damage.Distributions.ToList()) ?? [])
          .Select(item => new DistributionState(
            ProjectTarget(item.Target),
            item.Minimum,
            item.Maximum,
            item.Value
          ))
          .ToList();
        break;
    }

    return new AgentAction(
      id,
      action.Type.ToString(),
      action.Name,
      candidate.Source,
      targets,
      number,
      choices,
      order,
      distribution
    );
  }

  private static TargetSetState ProjectTargetSet(int index, TargetSet targetSet)
  {
    return new TargetSetState(
      index,
      Read(() => targetSet.Description),
      Read(() => targetSet.MinimumTargets),
      Read(() => targetSet.MaximumTargets),
      (Read(() => targetSet.LegalTargets.ToList()) ?? [])
        .Select(ProjectTarget)
        .ToList(),
      (Read(() => targetSet.CurrentTargets.ToList()) ?? [])
        .Select(ProjectTarget)
        .ToList()
    );
  }

  private static GameObjectState ProjectTarget(Targetable target)
  {
    var id = Read(() => target.Id);
    var fallback = Read(() => target.TargetInformation) ?? $"Object {id}";
    object? value = Read<object>(() => target.ToGameObject());
    return value switch
    {
      GameCard card => new GameObjectState("card", id, card.Name),
      GamePlayer player => new GameObjectState("player", id, player.Name),
      _ => new GameObjectState("object", id, fallback)
    };
  }

  private static Dictionary<int, ZonePosition> BuildZonePositions(
    Game game,
    IReadOnlyList<GameCard> snapshotCards
  )
  {
    var positions = new Dictionary<int, ZonePosition>();
    var groups = snapshotCards.GroupBy(card => new ZoneKey(
      Read(() => card.Zone?.Name),
      Read(() => card.OwnerIndex)
    ));

    foreach (var group in groups)
    {
      var representative = group
        .Select(card => Read(() => game.GetGameCard(card.Id)))
        .FirstOrDefault(card => card?.Zone is not null);
      var orderedCards = representative is null
        ? []
        : Read(() => representative.Zone!.Cards.ToList()) ?? [];
      var ordinalsByName = new Dictionary<string, int>(StringComparer.Ordinal);

      for (var index = 0; index < orderedCards.Count; index++)
      {
        var card = orderedCards[index];
        var name = Read(() => card.Name) ?? string.Empty;
        ordinalsByName.TryGetValue(name, out var ordinal);
        ordinalsByName[name] = ordinal + 1;
        if (card.Id > 0)
          positions[card.Id] = new ZonePosition(index, ordinal);
      }
    }

    return positions;
  }

  private static List<GameAction> ReadPromptActions(GamePrompt prompt) =>
    Read(() => prompt.Options.Values.SelectMany(actions => actions).ToList()) ?? [];

  private static T? Read<T>(Func<T> getter)
  {
    try
    {
      return getter();
    }
    catch
    {
      return default;
    }
  }

  public void Dispose()
  {
    GameProcessor.OnNewGame -= AttachGame;
    foreach (var game in _games.Values)
      game.ClearEvents();
    _games.Clear();
  }

  private sealed record ActionCandidate(GameAction Action, ActionSource Source);
  private sealed record ZoneKey(string? Zone, int OwnerIndex);
  private sealed record ZonePosition(int Index, int NameOrdinal);
}
