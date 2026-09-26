/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.API.Play;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Leagues;
using MTGOSDK.API.Play.Tournaments;
using MTGOSDK.Core.Remoting;


namespace AlphaMTG.MTGOAgent.Diagnostics;

/// <summary>
/// Read-only report of what the client will accept a click on, for the cards
/// in one game.
/// </summary>
/// <remarks>
/// This deliberately does not use the exported AgentState, and it resolves
/// every card through the game's own live card and its view model, so it
/// cannot report a card belonging to another game.
/// </remarks>
internal static class NativeActionsCommand
{
  public static int Run(string[] args)
  {
    try
    {
      int gameId = RequiredInt(args, "--game-id");
      int? cardId = OptionalInt(args, "--card-id");

      using ILoggerFactory loggerFactory = LoggerFactory.Create(
        builder => builder.AddSimpleConsole(options => options.SingleLine = true));
      using Process? process = RemoteClient.MTGOProcess()
        ?? throw new InvalidOperationException(
          "No running MTGO process was found. Start AlphaMTG first.");
      using var client = new Client(
        new ClientOptions
        {
          CreateProcess = false,
          CloseOnExit = false
        },
        loggerFactory: loggerFactory,
        process: process);

      if (!client.IsLoggedIn)
      {
        throw new InvalidOperationException(
          "The existing MTGO process is not logged in.");
      }

      Game game = FindGame(gameId);
      CardActionRegistry registry = game.CardActions;
      var cards = new List<CardSelectableReport>();

      foreach (int id in CardIds(game, cardId))
      {
        CardSelectable selectable = registry.GetSelectable(id);
        cards.Add(new CardSelectableReport(
          selectable.CardId,
          selectable.HasActions,
          selectable.IsSelectable,
          selectable.IsTargetable,
          selectable.IsTargeted,
          registry.GetActions(id).Count,
          selectable.Position?.X,
          selectable.Position?.Y));
      }

      Console.WriteLine(JsonSerializer.Serialize(
        new NativeActionsResult(gameId, cards), AgentJson.Options));
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }

  /// <summary>
  /// Card ids to report, taken from the joined match's own zones so the report
  /// is scoped to cards that are part of this game rather than found by
  /// scanning the client's managed heap.
  /// </summary>
  private static IEnumerable<int> CardIds(Game game, int? requested)
  {
    if (requested is int single)
    {
      yield return single;
      yield break;
    }

    foreach (GameZone zone in Zones(game))
    {
      foreach (GameCard card in zone.Cards)
      {
        yield return card.Id;
      }
    }
  }

  private static IEnumerable<GameZone> Zones(Game game)
  {
    foreach (GamePlayer player in game.Players)
    {
      foreach (CardZone zone in new[]
      {
        CardZone.Battlefield, CardZone.Hand, CardZone.Stack
      })
      {
        GameZone? resolved = Read(() => game.GetGameZone(player, zone));
        if (resolved is not null) yield return resolved;
      }
    }
  }

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

    throw new InvalidOperationException($"No live MTGO game {gameId} was found.");
  }

  private static T? Read<T>(Func<T> getter)
  {
    try { return getter(); }
    catch { return default; }
  }

  private static int RequiredInt(string[] args, string name) =>
    OptionalInt(args, name)
      ?? throw new ArgumentException($"{name} is required.");

  private static int? OptionalInt(string[] args, string name)
  {
    int index = Array.IndexOf(args, name);
    if (index < 0) return null;

    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value))
      throw new ArgumentException($"{name} requires an integer value.");

    return value;
  }

  private sealed record NativeActionsResult(
    int GameId,
    IReadOnlyList<CardSelectableReport> Cards);

  private sealed record CardSelectableReport(
    int CardId,
    bool HasActions,
    bool IsSelectable,
    bool IsTargetable,
    bool IsTargeted,
    int ActionCount,
    int? ScreenX,
    int? ScreenY);
}
