/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging;

using MTGOSDK.API.Play.Games;

using AlphaMTG.MTGOAgent.UIA.Actions;

namespace AlphaMTG.MTGOAgent;

/// <summary>
/// Stateful owner of the MTGO connection, game tracking, and execution.
/// </summary>
/// <remarks>
/// One attach for the process lifetime: the client, current game, registry,
/// and scene resolution all live here instead of per-call attaches, state
/// files, or instance locks.
/// </remarks>
internal sealed class Server : IDisposable
{
  private readonly ILoggerFactory _loggerFactory;
  private readonly ILogger _logger;
  private readonly MTGOSDK.API.Client _client;
  private readonly GameStateExporter _exporter;
  private readonly Router.DecisionLog _log = new();

  private Server(
    ILoggerFactory loggerFactory,
    ILogger logger,
    MTGOSDK.API.Client client,
    GameStateExporter exporter)
  {
    _loggerFactory = loggerFactory;
    _logger = logger;
    _client = client;
    _exporter = exporter;
  }

  public static async Task<Server> StartAsync(
    ILoggerFactory loggerFactory, ILogger logger)
  {
    MTGOSDK.API.Client client =
      await BotClient.StartAsync(loggerFactory, logger, logOn: true,
        closeOnExit: false);
    var exporter = new GameStateExporter(logger, client.CurrentUser.Id);
    exporter.Start();
    exporter.TrackCurrentGames();
    return new Server(loggerFactory, logger, client, exporter);
  }

  public int? CurrentGameId => _exporter.CurrentGameId;

  public Game Game(int gameId)
  {
    return _exporter.GetGame(gameId)
      ?? throw new InvalidOperationException(
        $"No live MTGO game {gameId} is tracked.");
  }

  public AgentState Snapshot(int gameId)
  {
    return _exporter.GetSnapshot(gameId)
      ?? throw new InvalidOperationException(
        $"No exported state for game {gameId} yet.");
  }

  public WPFSnapshot GetWPFSnapshot(int gameId)
  {
    return _exporter.GetWPFSnapshot(gameId)
      ?? throw new InvalidOperationException(
        $"No native snapshot for game {gameId} yet.");
  }

  public void RefreshNow(int gameId) => _exporter.RefreshNow(gameId);

  public int ResolveGameId(int? gameId)
  {
    if (gameId.HasValue) return gameId.Value;
    return CurrentGameId
      ?? throw new InvalidOperationException("No live MTGO game is tracked.");
  }

  public ActionExecutor Executor(int gameId)
  {
    AgentState snapshot = Snapshot(gameId);
    return new ActionExecutor(Game(gameId), snapshot);
  }

  public object ExecuteAction(
    int actionId,
    int nonce,
    int? gameId = null,
    int? targetSetId = null,
    int? targetId = null,
    int? choiceId = null,
    int? number = null,
    IReadOnlyList<int>? order = null,
    IReadOnlyList<UIA.Actions.DistributionInput>? distributions = null,
    string? wish = null)
  {
    int id = ResolveGameId(gameId);
    AgentState before = Snapshot(id);
    using ActionExecutor executor = Executor(id);
    UIA.Actions.TargetSelection? target =
      targetSetId.HasValue && targetId.HasValue
        ? new UIA.Actions.TargetSelection(targetSetId.Value, targetId.Value)
        : null;
    try
    {
      var result = executor.Execute(
        id, nonce, actionId, target, choiceId, number, order, distributions,
        wish);
      LogTick(id, before, result.Operation, result.Name, actionId,
        targetSetId, targetId, choiceId, number, order, wish);
      return result;
    }
    catch (Exception exception)
    {
      LogTick(id, before, "execute", $"action {actionId}",
        actionId, targetSetId, targetId, choiceId, number, order, wish,
        exception.Message.Split('\n')[0]);
      throw;
    }
  }

  public object SelectCard(int cardId, int? gameId = null)
  {
    int id = ResolveGameId(gameId);
    AgentState before = Snapshot(id);
    using ActionExecutor executor = Executor(id);
    try
    {
      var result = executor.Select(new UIA.Actions.SelectionTarget(cardId, null));
      LogTick(id, before, result.Operation, result.Name, 0,
        null, cardId, null, null, null, null);
      return result;
    }
    catch (Exception exception)
    {
      LogTick(id, before, "select-card", $"card {cardId}", 0,
        null, cardId, null, null, null, null,
        exception.Message.Split('\n')[0]);
      throw;
    }
  }

  public object SelectPlayer(string playerName, int? gameId = null)
  {
    int id = ResolveGameId(gameId);
    AgentState before = Snapshot(id);
    using ActionExecutor executor = Executor(id);
    try
    {
      var result = executor.Select(new UIA.Actions.SelectionTarget(null, playerName));
      LogTick(id, before, result.Operation, result.Name, 0,
        null, null, null, null, null, null);
      return result;
    }
    catch (Exception exception)
    {
      LogTick(id, before, "select-player", playerName, 0,
        null, null, null, null, null, null,
        exception.Message.Split('\n')[0]);
      throw;
    }
  }

  public object SelectDialog(string cardName, int? gameId = null)
  {
    int id = ResolveGameId(gameId);
    AgentState before = Snapshot(id);
    using ActionExecutor executor = Executor(id);
    try
    {
      var result = executor.SelectDialogCard(cardName);
      LogTick(id, before, result.Operation, result.Name, 0,
        null, null, null, null, null, null);
      return result;
    }
    catch (Exception exception)
    {
      LogTick(id, before, "select-dialog", cardName, 0,
        null, null, null, null, null, null,
        exception.Message.Split('\n')[0]);
      throw;
    }
  }

  public object PreviewCard(int cardId, int? gameId = null)
  {
    int id = ResolveGameId(gameId);
    using ActionExecutor executor = Executor(id);
    return executor.PreviewCard(cardId);
  }

  public object ClickControl(string label, int? gameId = null)
  {
    int id = ResolveGameId(gameId);
    AgentState before = Snapshot(id);
    using ActionExecutor executor = Executor(id);
    try
    {
      var result = executor.ExecuteControl(label, 0, "click-control");
      LogTick(id, before, result.Operation, result.Name, 0,
        null, null, null, null, null, null);
      return result;
    }
    catch (Exception exception)
    {
      LogTick(id, before, "click-control", label, 0,
        null, null, null, null, null, null,
        exception.Message.Split('\n')[0]);
      throw;
    }
  }

  private void LogTick(
    int gameId,
    AgentState before,
    string operation,
    string name,
    int actionId,
    int? targetSetId,
    int? targetId,
    int? choiceId,
    int? number,
    IReadOnlyList<int>? order,
    string? wish,
    string? note = null)
  {
    int? after = null;
    try
    {
      after = Snapshot(gameId).Nonce;
    }
    catch
    {
      // Best effort: the game may be gone.
    }
    _log.Record(new Router.TickRecord(
      DateTimeOffset.UtcNow,
      gameId,
      before.Nonce,
      after,
      before.Turn,
      before.Phase,
      before.Prompt.Text,
      before.Players
        .Select(player => new Router.PlayerRecord(
          player.Index, player.Name, player.Life))
        .ToList(),
      before.Cards
        .Select(card => new Router.CardRecord(
          card.Id, card.Name, card.Zone))
        .ToList(),
      before.Actions
        .Select(action => new Router.OfferedAction(
          action.Id, action.Type, action.Name, action.Source.CardId))
        .ToList(),
      new Router.ExecutedAction(
        operation, actionId, name, targetSetId, targetId, choiceId,
        number, order, wish),
      null,
      note));
  }

  public void Dispose()
  {
    _exporter.Dispose();
    _client.Dispose();
  }
}
