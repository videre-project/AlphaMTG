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
using MTGOSDK.API.Play.Games.Controls;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Games.Processors.EventArgs;
using MTGOSDK.Core.Reflection;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Reflection.Serialization;


namespace AlphaMTG.MTGOAgent;

internal sealed class GameStateExporter(ILogger logger, int localUserId) : IDisposable
{
  /// <summary>
  /// A WPF snapshot slower than this is worth reporting, because its
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
  private readonly ConcurrentDictionary<int, string> _lastUiRefreshes = new();
  private readonly ConcurrentDictionary<int, byte> _refreshQueued = new();
  private readonly ConcurrentDictionary<int, byte> _refreshDirty = new();
  private readonly ConcurrentDictionary<int, byte> _batchChecked = new();
  private readonly ConcurrentDictionary<int, byte> _batchBypass = new();
  private readonly ConcurrentDictionary<int, byte> _batchLogged = new();
  private readonly ConcurrentDictionary<int, AgentState> _documents = new();
  private readonly ConcurrentDictionary<int, WPFSnapshot> _wpfDocuments = new();
  private int _latestGameId;
  private CardResolver? _cards;
  private WPFSnapshotBuilder? _wpfTables;

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

    _latestGameId = game.Id;
    _documents.TryRemove(game.Id, out _);
    _wpfDocuments.TryRemove(game.Id, out _);

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
        "Tracking MTGO game {GameId} with action registry version {Version}.",
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

      GamePrompt? prompt = Try<GamePrompt?>(() => game.Prompt);
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
      PromptOptionsSnapshot promptOptions = prompt.CaptureOptions();
      long clientRevision = promptOptions.ClientRevision;
      string signature =
        $"{prompt.Nonce}:{prompt.Text}:{registry?.Version ?? 0}:{mode}:{clientRevision}";
      if (_lastUiRefreshes.TryGetValue(game.Id, out string? previous)
          && previous == signature)
      {
        return;
      }

      _lastUiRefreshes[game.Id] = signature;
      int? nonceOverride = snapshot.Nonce == prompt.Nonce
        ? null
        : prompt.Nonce;
      Export(game, snapshot, prompt, registry, nonceOverride, promptOptions);
    }
    catch (Exception exception)
    {
      logger.LogWarning(
        exception,
        "Failed to refresh MTGO game {GameId} after a UI or action change.",
        game.Id);
    }
  }

  private void Export(
    Game game,
    GameStateSnapshot snapshot,
    GamePrompt prompt,
    CardActionRegistry? registry,
    int? nonceOverride = null,
    PromptOptionsSnapshot? promptOptions = null)
  {
    try
    {
      // Resolve every card value once. Each property access below crosses
      // into the client, so reading each value once here (instead of once
      // per document, ordering, and grouping) is what keeps large boards
      // exportable. Everything below this line is local projection.
      _cards ??= new CardResolver(logger);
      _wpfTables ??= new WPFSnapshotBuilder(logger);
      var cards = _cards.ResolveCards(game, snapshot, registry);
      WPFSnapshot? table = _wpfTables.WriteWPFSnapshot(
        game, snapshot, prompt, registry, cards);
      if (table is not null)
        _wpfDocuments[game.Id] = table;

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
        nonceOverride,
        promptOptions);
      _documents[game.Id] = document;

      logger.LogInformation(
        "Refreshed game {GameId} state {Nonce} with {ActionCount} actions.",
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
    IReadOnlyList<ResolvedCard> resolved,
    int? nonceOverride = null,
    PromptOptionsSnapshot? promptOptionsOverride = null)
  {
    var cards = resolved
      .OrderBy(card => card.Zone ?? string.Empty)
      .ThenBy(card => card.OwnerIndex)
      .ThenBy(card => card.Id)
      .Select(ActionProjection.ProjectCard)
      .ToList();

    PromptOptionsSnapshot promptOptions =
      promptOptionsOverride ?? prompt.CaptureOptions();
    var candidates = new List<ActionCandidate>();
    foreach (var action in promptOptions.Options.Values
      .SelectMany(group => group).OfType<GameAction>())
    {
      if (action is null)
        continue;
      var source = action is CardAction cardAction
        ? new ActionSource("card", Try<int>(() => cardAction.Card.Id))
        : new ActionSource("prompt");
      candidates.Add(new ActionCandidate(action, source));
    }

    foreach (var card in resolved)
    {
      // The registry is the authoritative live action source. It retains
      // wrappers around the view-model action objects and is updated by each
      // card's collection event; do not rescan every live card here.
      foreach (var action in registry?.GetActions(card.Id, card.SourceId) ?? [])
      {
        if (action is not null)
          candidates.Add(new ActionCandidate(action, new ActionSource("card", card.Id)));
      }
    }

    var projected = new AgentAction?[candidates.Count];
    Parallel.For(0, candidates.Count, CardResolver.BatchOptions(candidates.Count), i =>
    {
      projected[i] = ActionProjection.TryProjectAction(i, candidates[i]);
    });
    // Prompt-box buttons (mana symbols for PayMana and color choices) are
    // neither card actions nor selections. Read them once and project the
    // enabled color buttons as choices so they are decidable and executable.
    // Choice ids are button positions; names are colors, which is what
    // execution matches on because positions are not stable across reads.
    var promptButtons = registry?.GetPromptButtons() ?? [];
    var manaChoices = promptButtons
      .Select((button, index) => (button, index))
      .Where(entry => entry.button.Enabled
        && entry.button.Color is not null)
      .Select(entry => new ActionChoice(
        entry.index, entry.button.Color!))
      .ToList();
    var actions = new List<AgentAction>(candidates.Count);
    foreach (var action in projected)
    {
      if (action is null)
        continue;
      if (action.Type == "PayMana" && action.Choices is null
        && manaChoices.Count > 0)
      {
        actions.Add(action with { Choices = manaChoices });
        continue;
      }
      actions.Add(action);
    }
    // A PayMana prompt renders only as symbol buttons with no backing
    // prompt action. Synthesize one so the choice is decidable.
    if (!actions.Any(candidate => candidate.Type == "PayMana")
      && manaChoices.Count > 0)
    {
      actions.Add(new AgentAction(
        candidates.Count,
        "PayMana",
        "Pay",
        new ActionSource("prompt"),
        Choices: manaChoices));
    }
    int nextActionId = actions.Count == 0
      ? 0
      : actions.Max(action => action.Id) + 1;
    foreach (PromptControl control in promptOptions.Options.Values
      .SelectMany(group => group).OfType<PromptControl>()
      .Where(control => control.Enabled
        && control.Id.Group == PromptButtonGroup.Standard))
    {
      actions.Add(new AgentAction(
        nextActionId++,
        control.Type.ToString(),
        control.Name,
        new ActionSource("control",
          ControlId: control.Id,
          ClientRevision: promptOptions.ClientRevision,
          ButtonType: control.ButtonType,
          Color: control.Color)));
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
          Try<string?>(() => entry.Value.Name) ?? string.Empty,
          Try<int>(() => entry.Value.Life),
          Try<bool>(() => entry.Value.HasPriority),
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
      snapshot.TurnNumber,
      snapshot.CurrentPhase.ToString(),
      new PromptState(
        prompt.Text,
        snapshot.Players.TryGetValue(prompt.PromptedPlayer, out var prompted)
          ? Try<string?>(() => prompted.Name)
          : null),
      players,
      cards,
      actions
    );
  }


  /// <summary>
  /// Latest exported document per tracked game, in memory. Replaces the
  /// former state-file handoff: tools read revisions here instead.
  /// </summary>
  public AgentState? GetSnapshot(int gameId) =>
    _documents.TryGetValue(gameId, out AgentState? document) ? document : null;

  public WPFSnapshot? GetWPFSnapshot(int gameId) =>
    _wpfDocuments.TryGetValue(gameId, out WPFSnapshot? document)
      ? document
      : null;

  public int? CurrentGameId => _games.IsEmpty ? null : _latestGameId;

  /// <summary>
  /// Tracks already-joined games. Event hooks only fire for new games, so
  /// a fresh session sweeps the current ones instead of waiting.
  /// </summary>
  public void TrackCurrentGames()
  {
    foreach (dynamic joined in MTGOSDK.API.Play.EventManager.JoinedEvents)
    {
      try
      {
        MTGOSDK.API.Play.Match? match = joined switch
        {
          MTGOSDK.API.Play.Leagues.League league => league.ActiveMatch,
          MTGOSDK.API.Play.Tournaments.Tournament tournament => tournament.ActiveMatch,
          MTGOSDK.API.Play.Match direct => direct,
          _ => null,
        };
        if (match?.CurrentGame is Game game)
          AttachGame(game);
      }
      catch
      {
        // Ignore events the client is transitioning away from.
      }
    }
  }

  /// <summary>
  /// Forces a synchronous refresh for a fresh session's cold exporter,
  /// then returns whatever it produced.
  /// </summary>
  public void RefreshNow(int gameId)
  {
    if (_games.TryGetValue(gameId, out Game? game) && game is not null)
      Refresh(game);
  }

  public Game? GetGame(int gameId) =>
    _games.TryGetValue(gameId, out Game? game) ? game : null;

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

}
