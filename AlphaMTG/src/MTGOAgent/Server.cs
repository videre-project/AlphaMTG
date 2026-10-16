/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using MTGOSDK.Core.Diagnostics;
using MTGOSDK.API.Play;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;


namespace AlphaMTG.MTGOAgent;

/// <summary>
/// Owns the MTGO connection and initializes SDK processing for joined games.
/// </summary>
internal sealed class Server : IDisposable
{
  private readonly MTGOSDK.API.Client _client;
  private readonly ILogger _logger;
  private readonly ConcurrentDictionary<int, Game> _games = new();
  private int _latestGameId;

  public int LocalUserId { get; }

  private Server(MTGOSDK.API.Client client, ILogger logger)
  {
    _client = client;
    _logger = logger;
    LocalUserId = client.CurrentUser.Id;
  }

  public static async Task<Server> StartAsync(
    ILoggerFactory loggerFactory, ILogger logger)
  {
    using var timing = LocalTelemetry.Begin("alpha.client.start");
    MTGOSDK.API.Client client = await BotClient.StartAsync(loggerFactory, logger);
    Server? server = null;
    try
    {
      server = new Server(client, logger);
      GameProcessor.OnNewGame += server.AttachGame;
      GameProcessor.EnsureHookInitialized();
      server.TrackCurrentGames();
      return server;
    }
    catch
    {
      if (server is not null) server.Dispose();
      else client.Dispose();
      throw;
    }
  }

  public Game Game(int? gameId)
  {
    int id = gameId ?? (_games.IsEmpty
      ? throw new InvalidOperationException("No live MTGO game is tracked.")
      : Volatile.Read(ref _latestGameId));
    return _games.GetValueOrDefault(id)
      ?? throw new InvalidOperationException($"No live MTGO game {id} is tracked.");
  }

  private void AttachGame(Game game)
  {
    int gameId = game.Id;
    if (!_games.TryAdd(gameId, game)) return;
    using var timing = LocalTelemetry.Begin("alpha.game.attach", gameId: gameId);

    try
    {
      var processor = GameProcessor.ForGame(game);
      processor.Register(new PromptProcessor());
      processor.Ready();
      Volatile.Write(ref _latestGameId, gameId);
      _logger.LogInformation("Tracking MTGO game {GameId}.", gameId);
    }
    catch
    {
      game.ClearEvents();
      _games.TryRemove(gameId, out _);
      throw;
    }
  }

  /// <summary>
  /// Finds already-joined games when connecting to an existing MTGO session.
  /// </summary>
  private void TrackCurrentGames()
  {
    foreach (dynamic joined in EventManager.JoinedEvents)
    {
      try
      {
        Match? match = joined switch
        {
          MTGOSDK.API.Play.Leagues.League league => league.ActiveMatch,
          MTGOSDK.API.Play.Tournaments.Tournament tournament => tournament.ActiveMatch,
          Match direct => direct,
          _ => null,
        };
        if (match?.CurrentGame is Game game) AttachGame(game);
      }
      catch (Exception error)
      {
        _logger.LogDebug(error, "Could not attach a joined MTGO event.");
      }
    }
  }

  public void Dispose()
  {
    GameProcessor.OnNewGame -= AttachGame;
    foreach (var game in _games.Values) game.ClearEvents();
    _games.Clear();
    _client.Dispose();
  }
}
