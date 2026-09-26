/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Games.Processors.EventArgs;
using MTGOSDK.Core.Reflection;


namespace AlphaMTG.MTGOAgent;

internal sealed class GameStateExporter(ILogger logger, int localUserId) : IDisposable
{
  private readonly ConcurrentDictionary<int, Game> _games = new();
  private readonly ConcurrentDictionary<int, GamePrompt> _prompts = new();
  private readonly ConcurrentDictionary<int, GameStateSnapshot> _snapshots = new();
  private readonly object _writeLock = new();
  private readonly ConcurrentDictionary<int, string> _lastUiRefreshes = new();
  private readonly ConcurrentDictionary<int, byte> _refreshQueued = new();
  private readonly string _outputPath = AgentStateStore.DefaultPath;
  private readonly string _nativeUiOutputPath =
    Environment.GetEnvironmentVariable("ALPHAMTG_NATIVE_UI_STATE_PATH")
    ?? Path.Combine("AlphaMTG", "native-ui.json");

  public void Start()
  {
    GameProcessor.OnNewGame += AttachGame;
    GameProcessor.EnsureHookInitialized();
    logger.LogInformation("MTGO game-state exporter is ready.");
  }

  private void AttachGame(Game game)
  {
    if (!_games.TryAdd(game.Id, game))
      return;

    try
    {
      CardActionRegistry registry = game.CardActions;

      // The SDK registry is attached before the built-in processor events so
      // the exporter has an authoritative action view for each snapshot.
      game.CardActionsChanged += _ => ScheduleRefresh(game);

      game.OnPromptChanged += args =>
      {
        if (args.Nonce != args.Snapshot.Nonce || args.Nonce != args.Prompt.Nonce)
          return;

        _prompts[game.Id] = args.Prompt;
        _snapshots[game.Id] = args.Snapshot;
        Export(game, args.Snapshot, args.Prompt, registry);
      };
      game.OnCardChanged += args => ExportCurrent(game, args);
      game.OnPlayerChanged += args => ExportCurrent(game, args);
      game.OnZoneChanged += args => ExportCurrent(game, args);
      game.ReadyProcessor();
      logger.LogInformation(
        "Tracking MTGO game {GameId} with native action registry version {Version}.",
        game.Id,
        registry.Version);
    }
    catch
    {
      game.ClearEvents();
      _games.TryRemove(game.Id, out _);
      throw;
    }
  }

  private void ExportCurrent(Game game, GameEventArgs args)
  {
    _snapshots[game.Id] = args.Snapshot;
    if (_prompts.TryGetValue(game.Id, out var prompt)
        && prompt.Nonce == args.Nonce)
    {
      Export(game, args.Snapshot, prompt, game.CardActions);
    }
  }

  private void RefreshGames()
  {
    foreach (Game game in _games.Values)
      Refresh(game);
  }

  private void ScheduleRefresh(Game game)
  {
    // Registry notifications are delivered from the SDK remoting/processor
    // thread. Never call WaitForPendingProcessing synchronously from that
    // thread, because the drain task may be waiting on this callback.
    if (!_refreshQueued.TryAdd(game.Id, 0))
      return;

    _ = Task.Run(() =>
    {
      try
      {
        if (_games.ContainsKey(game.Id))
          Refresh(game);
      }
      finally
      {
        _refreshQueued.TryRemove(game.Id, out _);
      }
    });
  }

  private void Refresh(Game game)
  {
    try
    {
      // A UI transition can precede the SDK's set_Prompt/gameplay-status
      // callbacks. Drain both queues before deciding that the model is stale.
      game.WaitForPendingProcessing(TimeSpan.FromSeconds(1));

      GamePrompt? prompt = Read(() => game.Prompt);
      if (prompt is null)
        return;

      if (!_snapshots.TryGetValue(game.Id, out GameStateSnapshot? snapshot)
          || snapshot is null)
      {
        return;
      }

      CardActionRegistry? registry = game.CardActions;
      // The native/UI dump is independent of the server snapshot. In
      // particular, a CollectionChanged notification can be the only state
      // transition after a local menu or mode interaction.
      WriteNativeUiSnapshot(game, snapshot, prompt, registry);

      // Include the registry version so an action-only collection mutation
      // produces a fresh export even when the prompt text and nonce are
      // unchanged.
      string signature = $"{prompt.Nonce}:{prompt.Text}:{registry?.Version ?? 0}";
      if (_lastUiRefreshes.TryGetValue(game.Id, out string? previous)
          && previous == signature)
      {
        return;
      }

      _lastUiRefreshes[game.Id] = signature;
      int? nonceOverride = snapshot.Nonce == prompt.Nonce
        ? null
        : prompt.Nonce;
      Export(game, snapshot, prompt, registry, nonceOverride);
    }
    catch (Exception exception)
    {
      logger.LogWarning(
        exception,
        "Failed to refresh MTGO game {GameId} after a UI or native-action change.",
        game.Id);
    }
  }

  private void Export(
    Game game,
    GameStateSnapshot snapshot,
    GamePrompt prompt,
    CardActionRegistry? registry,
    int? nonceOverride = null)
  {
    try
    {
      // Always retain a native/UI view, including when it cannot yet be
      // correlated with a new server snapshot or belongs to another player.
      WriteNativeUiSnapshot(game, snapshot, prompt, registry);

      if (snapshot.Nonce != prompt.Nonce && nonceOverride is null)
        return;

      if (!snapshot.Players.TryGetValue(
            prompt.PromptedPlayer,
            out var player)
          || player.UserId != localUserId)
      {
        return;
      }

      // Solitaire does not consistently mark its sole player as having priority.
      if (snapshot.Players.Count > 1 && !player.HasPriority)
        return;

      var document = BuildDocument(
        game,
        snapshot,
        prompt,
        registry,
        nonceOverride);
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
    GamePrompt prompt,
    CardActionRegistry? registry,
    int? nonceOverride = null)
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
      if (action is null)
        continue;
      var source = action is CardAction cardAction
        ? new ActionSource("card", Read(() => cardAction.Card.Id))
        : new ActionSource("prompt");
      candidates.Add(new ActionCandidate(action, source));
    }

    foreach (var card in cards)
    {
      // The registry is the authoritative live action source. It retains
      // wrappers around the native action objects and is updated by each
      // card's collection event; do not rescan every live card here.
      foreach (var action in registry?.GetActions(card.Id, card.SourceId) ?? [])
      {
        if (action is not null)
          candidates.Add(new ActionCandidate(action, new ActionSource("card", card.Id)));
      }
    }

    var actions = new List<AgentAction>();
    foreach (var candidate in candidates)
    {
      var projected = TryProjectAction(actions.Count, candidate);
      if (projected is not null)
        actions.Add(projected);
    }
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
      nonceOverride ?? snapshot.Nonce,
      snapshot.Timestamp,
      snapshot.TurnNumber,
      snapshot.CurrentPhase.ToString(),
      new PromptState(prompt.Text, prompt.PromptedPlayer),
      players,
      cards,
      actions
    );
  }

  private void WriteNativeUiSnapshot(
    Game game,
    GameStateSnapshot snapshot,
    GamePrompt prompt,
    CardActionRegistry? registry)
  {
    try
    {
      var cards = snapshot.Cards.Values
        .OrderBy(card => Read(() => card.Zone?.Name) ?? string.Empty)
        .ThenBy(card => Read(() => card.Id))
        .Select(card => ProjectNativeCard(card, registry))
        .ToList();
      var document = new NativeUiSnapshot(
        DateTimeOffset.UtcNow,
        game.Id,
        snapshot.Nonce,
        prompt.Nonce,
        Read(() => prompt.Text),
        registry?.Version ?? 0,
        cards);

      lock (_writeLock)
        AgentStateStore.Write(_nativeUiOutputPath, document);
    }
    catch (Exception exception)
    {
      logger.LogWarning(
        exception,
        "Failed to write native UI snapshot for MTGO game {GameId}.",
        game.Id);
    }
  }

  private static NativeCardSnapshot ProjectNativeCard(
    GameCard card,
    CardActionRegistry? registry)
  {
    var actions = registry?.GetActions(card.Id, card.SourceId) ?? [];
    var selectable = registry?.GetSelectable(card.Id, card.SourceId);
    return new NativeCardSnapshot(
      card.Id,
      card.SourceId,
      Read(() => card.Name),
      Read(() => card.Zone?.Name),
      Read(() => card.IsAttacking),
      Read(() => card.IsTapped),
      selectable?.IsSelectable ?? false,
      selectable?.IsTargetable ?? false,
      selectable?.IsTargeted ?? false,
      selectable?.Position?.X,
      selectable?.Position?.Y,
      actions
        .Select((action, index) => ProjectNativeAction(action, index))
        .ToList());
  }

  private static NativeActionSnapshot ProjectNativeAction(
    CardAction action,
    int index)
  {
    dynamic? rawAction = null;
    try
    {
      rawAction = DLRWrapper.Unbind(action);
    }
    catch
    {
      // The derived projection below will use safe defaults for stale actions.
    }

    return new NativeActionSnapshot(
      index,
      Read(() => action.ActionId),
      Read(() => action.Type.ToString()) ?? "Unknown",
      Read(() => action.Name) ?? string.Empty,
      Read<string?>(() => Convert.ToString(rawAction?.GroupName)),
      Read<string?>(() => Convert.ToString(rawAction?.ActionChoices)),
      Read<string?>(() => Convert.ToString(rawAction?.ModeChoiceMapping)),
      Read(() => action.Targets.Count),
      Read(() => (bool)(rawAction?.CanBePerformedLocally ?? false)),
      Read(() => (bool)(rawAction?.AltMenuAction ?? false)),
      Read(() => Convert.ToInt32(rawAction?.AttackVictimId ?? 0)));
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

  private static AgentAction? TryProjectAction(
    int id,
    ActionCandidate candidate)
  {
    try
    {
      return ProjectAction(id, candidate);
    }
    catch
    {
      // A native action can be removed between a collection notification and
      // this derived export. Do not let one stale wrapper hide the remaining
      // live actions or trigger an export failure loop.
      return null;
    }
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
        choices = (Read(() => selectPlayer.AvailablePlayers
          .OfType<GamePlayer>()
          .ToList()) ?? [])
          .Select(player => new ActionChoice(
            Read(() => player.UserId),
            Read(() => player.Name) ?? string.Empty))
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
      Read(() => action.Type.ToString()) ?? action.GetType().Name,
      Read(() => action.Name) ?? string.Empty,
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
    Read(() => prompt.Options.Values
      .SelectMany(actions => actions)
      .Where(action => action is not null)
      .ToList()) ?? [];

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
    _prompts.Clear();
    _snapshots.Clear();
    _lastUiRefreshes.Clear();
    _refreshQueued.Clear();
  }

  private sealed record ActionCandidate(GameAction Action, ActionSource Source);
  private sealed record ZoneKey(string? Zone, int OwnerIndex);
  private sealed record ZonePosition(int Index, int NameOrdinal);
}
