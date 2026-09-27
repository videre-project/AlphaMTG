/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Games.Processors.EventArgs;
using MTGOSDK.Core.Reflection;
using MTGOSDK.Core.Reflection.Serialization;


namespace AlphaMTG.MTGOAgent;

internal sealed class GameStateExporter(ILogger logger, int localUserId) : IDisposable
{
  /// <summary>
  /// A native UI snapshot slower than this is worth reporting, because its
  /// cost is paid on the MTGO client's UI thread.
  /// </summary>
  private static readonly TimeSpan s_slowNativeUiWrite =
    TimeSpan.FromMilliseconds(250);

  /// <summary>
  /// A state refresh slower than this is worth reporting, because it holds up
  /// every later refresh for the same game.
  /// </summary>
  private static readonly TimeSpan s_slowRefresh =
    TimeSpan.FromSeconds(2);

  private readonly ConcurrentDictionary<int, Game> _games = new();
  private readonly ConcurrentDictionary<int, GamePrompt> _prompts = new();
  private readonly ConcurrentDictionary<int, GameStateSnapshot> _snapshots = new();
  private readonly object _writeLock = new();
  private readonly ConcurrentDictionary<int, string> _lastUiRefreshes = new();
  private readonly ConcurrentDictionary<int, byte> _refreshQueued = new();
  private readonly ConcurrentDictionary<int, byte> _refreshDirty = new();
  private readonly ConcurrentDictionary<int, byte> _batchChecked = new();
  private readonly ConcurrentDictionary<int, byte> _batchBypass = new();
  private readonly ConcurrentDictionary<int, byte> _batchLogged = new();
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

  /// <summary>
  /// Re-exports a game whose duel scene changed interaction mode.
  /// </summary>
  /// <remarks>
  /// Entering and leaving target selection produces no prompt and no state
  /// snapshot, so no prompt or card event fires while MTGO is waiting for a
  /// target. The interaction mode is the only signal for that transition, and
  /// it is what makes the targetable cards and players in the snapshot
  /// meaningful.
  /// </remarks>
  private void OnInteractionModeChanged(Game game, InteractionMode mode)
  {
    if (!_games.ContainsKey(game.Id))
      return;

    logger.LogInformation(
      "MTGO game {GameId} entered interaction mode {Mode}.",
      game.Id,
      mode);

    ScheduleRefresh(game);
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

      // Target selection changes the interaction mode without setting a prompt
      // or sending a snapshot, so this is the only notification of it.
      //
      // Attached best-effort. This runs inside the remoting callback that
      // discovers games, so an exception here propagates into the
      // communicator and silently stops every game from ever being tracked. A
      // tracker that cannot attach must not take the exporter down with it.
      try
      {
        game.InteractionModeChanged +=
          mode => OnInteractionModeChanged(game, mode);

        if (game.InteractionState.AttachFailure is Exception failure)
        {
          logger.LogWarning(
            failure,
            "Interaction-mode tracking for MTGO game {GameId} could not hook "
              + "the duel scene and will not report target selection.",
            game.Id);
        }
        else
        {
          logger.LogInformation(
            "MTGO game {GameId} is tracking duel-scene interaction mode.",
            game.Id);
        }
      }
      catch (Exception exception)
      {
        logger.LogWarning(
          exception,
          "Could not attach interaction-mode tracking for MTGO game {GameId}. "
            + "The exporter will keep working, but it will not see target "
            + "selection start or end.",
          game.Id);
      }

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

  private void ScheduleRefresh(Game game)
  {
    // Registry notifications are delivered from the SDK remoting/processor
    // thread. Never call WaitForPendingProcessing synchronously from that
    // thread, because the drain task may be waiting on this callback.
    //
    // A refresh already in flight does not drop this one: it marks the game
    // dirty, and the running task drains dirt until clean. Dropping here
    // would lose state transitions that arrive while a refresh is running,
    // which on a busy board is most of them.
    _refreshDirty[game.Id] = 0;
    if (!_refreshQueued.TryAdd(game.Id, 0))
      return;

    _ = Task.Run(() =>
    {
      var started = Stopwatch.GetTimestamp();
      try
      {
        while (_refreshDirty.TryRemove(game.Id, out _))
        {
          if (!_games.ContainsKey(game.Id))
            break;

          Refresh(game);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed > s_slowRefresh)
        {
          logger.LogWarning(
            "State refresh for MTGO game {GameId} took {ElapsedMs} ms.",
            game.Id,
            (int)elapsed.TotalMilliseconds);
        }
      }
      finally
      {
        _refreshQueued.TryRemove(game.Id, out _);
        // Dirt that arrived between the last drain and this removal would
        // otherwise sit with no runner. Re-queueing covers it; the extra run
        // is cheap when nothing changed.
        if (_refreshDirty.TryRemove(game.Id, out _)
            && _games.ContainsKey(game.Id))
        {
          ScheduleRefresh(game);
        }
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
      // Include the registry version and the interaction mode so a change in
      // either produces a fresh export even when the prompt text and nonce
      // are unchanged. The mode matters most: entering target selection
      // changes neither the prompt nor the nonce, but it does change which
      // cards and players are targetable. Every refresh trigger (action-list
      // change, mode change, prompt change) moves at least one of these, so
      // an unchanged signature means a redundant delivery and is safe to skip.
      InteractionMode mode = game.InteractionState.Mode;
      string signature =
        $"{prompt.Nonce}:{prompt.Text}:{registry?.Version ?? 0}:{mode}";
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
      // Resolve every card value once. Each property access below crosses
      // into the client, so reading each value once here (instead of once
      // per document, ordering, and grouping) is what keeps large boards
      // exportable. Everything below this line is local projection.
      var cards = ResolveCards(game, snapshot, registry);
      WriteNativeUiSnapshot(game, snapshot, prompt, registry, cards);

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
        cards,
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

  /// <summary>
  /// Everything both exported documents need for one card, read once per
  /// export. Every property access on a live wrapper crosses into the
  /// client, so resolving each value here and projecting locally below is
  /// what keeps large boards exportable.
  /// </summary>
  private sealed record ResolvedCard(
    GameCard Card,
    int Id,
    int SourceId,
    int Ctn,
    string? Zone,
    int OwnerIndex,
    int ControllerIndex,
    string? Name,
    bool IsAttacking,
    bool IsTapped,
    CardSelectable? Selectable,
    IReadOnlyList<CardAction> Actions);

  /// <summary>
  /// Bounded parallelism for per-card and per-action remote reads.
  /// </summary>
  /// <remarks>
  /// Mirrors the SDK's own batch discipline: one parallel level at a time,
  /// per-item work sequential inside. Callers must never nest one parallel
  /// loop inside another.
  /// </remarks>
  private static ParallelOptions BatchOptions(int count) =>
    new()
    {
      MaxDegreeOfParallelism = Math.Max(
        1, Math.Min(count, Environment.ProcessorCount))
    };

  /// <summary>
  /// Batch paths resolved per zone in a single IPC call each. Zone and owner
  /// come free from batch context, so they are not member reads.
  /// </summary>
  private static readonly string[] s_batchPaths =
  [
    "Id", "Name",
    "Tag.CardViewModel.HasActions", "Tag.CardViewModel.IsSelectable",
    "Tag.CardViewModel.IsTargetable", "Tag.CardViewModel.IsTargeted",
  ];

  private static readonly CardZone[] s_batchZones =
  [
    CardZone.Battlefield, CardZone.Hand, CardZone.Stack,
    CardZone.Graveyard, CardZone.Exile,
  ];

  /// <summary>
  /// One row of zone-batch data: everything the batch endpoint served.
  /// </summary>
  private sealed record BatchedRow(
    string? Zone,
    int OwnerIndex,
    string? Name,
    bool HasActions,
    bool IsSelectable,
    bool IsTargetable,
    bool IsTargeted);

  private IReadOnlyList<ResolvedCard> ResolveCards(
    Game game,
    GameStateSnapshot snapshot,
    CardActionRegistry? registry)
  {
    var batched = ReadZoneBatches(game, snapshot);

    // Once per game, prove the batch path faithful by comparing a few rows
    // against the proven single-read path. On divergence the game bypasses
    // batching permanently (correctness first); the per-card fallback below
    // is today's behavior.
    if (registry is not null
        && batched.Count > 0
        && !_batchChecked.ContainsKey(game.Id)
        && !_batchBypass.ContainsKey(game.Id))
    {
      _batchChecked[game.Id] = 0;
      if (!ValidateBatch(game, registry, batched))
      {
        _batchBypass[game.Id] = 0;
        logger.LogWarning(
          "Zone-batch reads diverged from single reads for MTGO game "
            + "{GameId}; batching bypassed for this game.",
          game.Id);
      }
      else
      {
        logger.LogInformation(
          "Zone-batch reads engaged for MTGO game {GameId} "
            + "({CardCount} cards across zones).",
          game.Id,
          batched.Count);
      }
    }

    bool useBatch = registry is not null
      && batched.Count > 0
      && !_batchBypass.ContainsKey(game.Id);

    var cards = snapshot.Cards.Values.ToList();
    var resolved = new ResolvedCard[cards.Count];
    Parallel.For(0, cards.Count, BatchOptions(cards.Count), i =>
    {
      GameCard card = cards[i];
      int id = card.Id;
      int sourceId = card.SourceId;

      if (useBatch && batched.TryGetValue(id, out BatchedRow? row))
      {
        resolved[i] = new ResolvedCard(
          card,
          id,
          sourceId,
          Read(() => card.CTN),
          row.Zone,
          row.OwnerIndex,
          Read(() => card.ControllerIndex),
          row.Name ?? Read(() => card.Name),
          Read(() => card.IsAttacking),
          Read(() => card.IsTapped),
          new CardSelectable(
            id, row.HasActions, row.IsSelectable, row.IsTargetable,
            row.IsTargeted),
          registry?.GetActions(id, sourceId) ?? []);
        return;
      }

      // Fallback: today's full per-card reads, for cards no batch covered
      // and for games where batching proved unfaithful.
      CardSelectable? selectable = registry?.GetSelectable(id);
      resolved[i] = new ResolvedCard(
        card,
        id,
        sourceId,
        Read(() => card.CTN),
        Read(() => card.Zone?.Name),
        Read(() => card.OwnerIndex),
        Read(() => card.ControllerIndex),
        Read(() => card.Name),
        Read(() => card.IsAttacking),
        Read(() => card.IsTapped),
        selectable,
        registry?.GetActions(id, sourceId) ?? []);
    });

    return resolved;
  }

  /// <summary>
  /// Reads one batch per player zone. Zone and owner come from context, so
  /// they cost no member reads.
  /// </summary>
  private Dictionary<int, BatchedRow> ReadZoneBatches(
    Game game,
    GameStateSnapshot snapshot)
  {
    var batched = new ConcurrentDictionary<int, BatchedRow>();

    List<GamePlayer> players;
    try
    {
      players = game.Players.ToList();
    }
    catch
    {
      return new Dictionary<int, BatchedRow>();
    }

    // Snapshot player index by account, so batch context maps to it without
    // per-card owner reads. A user id that reads as 0 (seen live) never
    // matches, so fall back to list position, which aligns with snapshot
    // keys by construction.
    var ownerByUser = new Dictionary<int, int>();
    foreach (var entry in snapshot.Players)
    {
      int userId = Read(() => entry.Value.UserId);
      ownerByUser[userId] = entry.Key;
    }

    var pairs = new List<(GamePlayer Player, int OwnerIndex, CardZone Zone)>();
    for (int i = 0; i < players.Count; i++)
    {
      int userId = Read(() => players[i].UserId);
      if (!ownerByUser.TryGetValue(userId, out int ownerIndex))
        ownerIndex = i;

      foreach (CardZone zone in s_batchZones)
        pairs.Add((players[i], ownerIndex, zone));
    }

    int zonesOk = 0;
    int rowsCollected = 0;
    Parallel.For(0, pairs.Count, BatchOptions(pairs.Count), p =>
    {
      var (player, ownerIndex, zone) = pairs[p];
      GameZone? resolved = Read(() => game.GetGameZone(player, zone));
      if (resolved is null) return;

      string? zoneName = Read(() => resolved.Name);
      if (!resolved.TryGetBatchSnapshot(s_batchPaths, out var batch)
          || batch is null)
      {
        return;
      }

      Interlocked.Increment(ref zonesOk);
      for (int row = 0; row < batch.Count; row++)
      {
        int id = batch.GetInt(row, "Id");
        if (id <= 0) continue;

        if (batched.TryAdd(id, new BatchedRow(
          zoneName,
          ownerIndex,
          batch.GetString(row, "Name", null!),
          ParseBatchBool(batch, row, "Tag.CardViewModel.HasActions"),
          ParseBatchBool(batch, row, "Tag.CardViewModel.IsSelectable"),
          ParseBatchBool(batch, row, "Tag.CardViewModel.IsTargetable"),
          ParseBatchBool(batch, row, "Tag.CardViewModel.IsTargeted"))))
        {
          Interlocked.Increment(ref rowsCollected);
        }
      }
    });

    // One line per game: silent batch failure is otherwise indistinguishable
    // from an empty board.
    if (_batchLogged.TryAdd(game.Id, 0))
    {
      logger.LogInformation(
        "Zone-batch reads for MTGO game {GameId}: {Zones} zones, {Rows} rows.",
        game.Id,
        zonesOk,
        rowsCollected);
    }

    return new Dictionary<int, BatchedRow>(batched);
  }

  private static bool ParseBatchBool(
    BatchCollectionSnapshot batch, int row, string path) =>
    bool.TryParse(batch.GetString(row, path, null!), out bool value) && value;

  /// <summary>
  /// Compares batch flags against the proven single-read path for a few
  /// cards. Agreement means the batch endpoint is faithful for this game;
  /// any divergence bypasses batching permanently.
  /// </summary>
  private bool ValidateBatch(
    Game game,
    CardActionRegistry registry,
    Dictionary<int, BatchedRow> batched)
  {
    int compared = 0;
    foreach (int id in batched.Keys.Take(3))
    {
      CardSelectable single;
      try
      {
        single = registry.GetSelectable(id);
      }
      catch
      {
        return false;
      }

      BatchedRow row = batched[id];
      if (single.HasActions != row.HasActions
          || single.IsSelectable != row.IsSelectable
          || single.IsTargetable != row.IsTargetable
          || single.IsTargeted != row.IsTargeted)
      {
        return false;
      }

      compared++;
    }

    return compared > 0;
  }


  private static AgentState BuildDocument(
    Game game,
    GameStateSnapshot snapshot,
    GamePrompt prompt,
    CardActionRegistry? registry,
    IReadOnlyList<ResolvedCard> resolved,
    int? nonceOverride = null)
  {
    var zonePositions = BuildZonePositions(game, resolved);
    var cards = resolved
      .OrderBy(card => card.Zone ?? string.Empty)
      .ThenBy(card => card.OwnerIndex)
      .ThenBy(card => zonePositions.GetValueOrDefault(card.Id)?.Index ?? int.MaxValue)
      .ThenBy(card => card.Id)
      .Select(card => ProjectCard(card, zonePositions.GetValueOrDefault(card.Id)))
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

    var projected = new AgentAction?[candidates.Count];
    Parallel.For(0, candidates.Count, BatchOptions(candidates.Count), i =>
    {
      projected[i] = TryProjectAction(i, candidates[i]);
    });
    var actions = new List<AgentAction>(candidates.Count);
    foreach (var action in projected)
    {
      if (action is not null)
        actions.Add(action);
    }
    // Player click state comes from the duel scene's own player view models, so
    // it reflects the same targetability pass that sets card flags. Matched on
    // the game's own player index, which is the key the snapshot uses too.
    var selectableByIndex = registry?
      .GetPlayerSelectable()
      .ToDictionary(entry => entry.PlayerIndex)
      ?? [];

    var players = snapshot.Players
      .OrderBy(entry => entry.Key)
      .Select(entry =>
      {
        var selectable = selectableByIndex.GetValueOrDefault(entry.Key);
        return new PlayerState(
          entry.Key,
          Read(() => entry.Value.UserId),
          Read(() => entry.Value.Name) ?? string.Empty,
          Read(() => entry.Value.Life),
          Read(() => entry.Value.HasPriority),
          selectable?.IsTargetable ?? false,
          selectable?.IsTargeted ?? false,
          selectable?.IsActive ?? false
        );
      })
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
    CardActionRegistry? registry,
    IReadOnlyList<ResolvedCard> resolved)
  {
    try
    {
      var started = Stopwatch.GetTimestamp();
      var ordered = resolved
        .OrderBy(card => card.Zone ?? string.Empty)
        .ThenBy(card => card.Id)
        .ToList();
      var natives = new NativeCardSnapshot[ordered.Count];
      Parallel.For(0, ordered.Count, BatchOptions(ordered.Count), i =>
      {
        natives[i] = ProjectNativeCard(ordered[i]);
      });
      IReadOnlyList<NativeCardSnapshot> cards = natives;
      var document = new NativeUiSnapshot(
        DateTimeOffset.UtcNow,
        game.Id,
        snapshot.Nonce,
        prompt.Nonce,
        Read(() => prompt.Text),
        registry?.Version ?? 0,
        game.InteractionState.Mode.ToString(),
        game.InteractionState.PromptBoxText,
        cards);

      lock (_writeLock)
        AgentStateStore.Write(_nativeUiOutputPath, document);

      // These reads cross onto the client's UI thread, so the time they take
      // is the thing to watch when the exporter falls behind.
      var elapsed = Stopwatch.GetElapsedTime(started);
      if (elapsed > s_slowNativeUiWrite)
      {
        logger.LogWarning(
          "Native UI snapshot for MTGO game {GameId} took {ElapsedMs} ms "
            + "for {CardCount} cards.",
          game.Id,
          (int)elapsed.TotalMilliseconds,
          cards.Count);
      }
    }
    catch (Exception exception)
    {
      logger.LogWarning(
        exception,
        "Failed to write native UI snapshot for MTGO game {GameId}.",
        game.Id);
    }
  }

  private static NativeCardSnapshot ProjectNativeCard(ResolvedCard card)
  {
    return new NativeCardSnapshot(
      card.Id,
      card.SourceId,
      card.Name,
      card.Zone,
      card.IsAttacking,
      card.IsTapped,
      card.Selectable?.IsSelectable ?? false,
      card.Selectable?.IsTargetable ?? false,
      card.Selectable?.IsTargeted ?? false,
      card.Actions
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

  private static CardState ProjectCard(
    ResolvedCard card,
    ZonePosition? position)
  {
    return new CardState(
      card.Id,
      card.SourceId,
      card.Ctn,
      card.Name ?? string.Empty,
      card.Zone,
      card.OwnerIndex,
      card.ControllerIndex,
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
    IReadOnlyList<ResolvedCard> resolved
  )
  {
    var positions = new ConcurrentDictionary<int, ZonePosition>();
    var groups = resolved.GroupBy(card => new ZoneKey(
      card.Zone,
      card.OwnerIndex
    )).ToList();

    // Independent zone groups resolve in parallel; members within a group
    // stay sequential.
    Parallel.For(0, groups.Count, BatchOptions(groups.Count), g =>
    {
      var group = groups[g];
      var representative = group
        .FirstOrDefault(card => card.Zone is not null)
        ?.Card;
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
    });

    return new Dictionary<int, ZonePosition>(positions);
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
    _refreshDirty.Clear();
    _batchChecked.Clear();
    _batchBypass.Clear();
    _batchLogged.Clear();
  }

  private sealed record ActionCandidate(GameAction Action, ActionSource Source);
  private sealed record ZoneKey(string? Zone, int OwnerIndex);
  private sealed record ZonePosition(int Index, int NameOrdinal);
}
