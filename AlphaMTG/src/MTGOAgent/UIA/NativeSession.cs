/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.API.Play;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Leagues;
using MTGOSDK.API.Play.Tournaments;
using MTGOSDK.Core.Remoting;


namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// A short-lived attachment to the running MTGO client, used to resolve things
/// natively at the moment of an action.
/// </summary>
/// <remarks>
/// This is not the agent. It writes no state files and takes no instance lock;
/// it exists so a diagnostic or click can read the client's own view models
/// rather than a snapshot of them.
/// </remarks>
internal sealed class NativeSession : IDisposable
{
  private readonly Client _client;
  private readonly Game _game;

  private NativeSession(Client client, Game game)
  {
    _client = client;
    _game = game;
  }

  public CardActionRegistry Registry => _game.CardActions;

  public int GameId => _game.Id;

  /// <summary>
  /// Attaches to the running client and locates the game the state file
  /// describes.
  /// </summary>
  public static NativeSession Attach(string statePath)
  {
    int gameId = AgentStateStore.Read(statePath).GameId;

    using ILoggerFactory loggerFactory = LoggerFactory.Create(
      builder => builder.AddSimpleConsole(options => options.SingleLine = true));

    using Process? process = RemoteClient.MTGOProcess()
      ?? throw new InvalidOperationException(
        "No running MTGO process was found. Start AlphaMTG first.");

    var client = new Client(
      new ClientOptions
      {
        CreateProcess = false,
        CloseOnExit = false
      },
      loggerFactory: loggerFactory,
      process: process);

    if (!client.IsLoggedIn)
    {
      client.Dispose();
      throw new InvalidOperationException(
        "The existing MTGO process is not logged in.");
    }

    return new NativeSession(client, FindGame(gameId));
  }

  /// <summary>
  /// Finds the current game through the joined match rather than by scanning,
  /// so it cannot resolve a game that is not the one being played.
  /// </summary>
  private static Game FindGame(int gameId)
  {
    foreach (dynamic joined in EventManager.JoinedEvents)
    {
      try
      {
        Match? match = joined switch
        {
          League league => league.ActiveMatch,
          Tournament tournament => tournament.ActiveMatch,
          Match direct => direct,
          _ => null
        };

        if (match?.CurrentGame?.Id == gameId) return match.CurrentGame;
      }
      catch
      {
        // Ignore events the client is transitioning away from.
      }
    }

    throw new InvalidOperationException(
      $"No live MTGO game {gameId} was found.");
  }

  public void Dispose() => _client.Dispose();
}
