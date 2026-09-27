/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;
using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.API.Play;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Leagues;
using MTGOSDK.API.Play.Tournaments;
using MTGOSDK.Core.Reflection;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Types;


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
      string? cardIds = OptionalValue(args, "--card-ids")
        ?? OptionalInt(args, "--card-id")?.ToString();
      bool includePosition =
        Array.IndexOf(args, "--with-position") >= 0;

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

      if (Array.IndexOf(args, "--batch-probe") >= 0)
        return BatchProbe(game);

      if (Array.IndexOf(args, "--selectors") >= 0)
      {
        var dialogs = registry.GetSelectorDialogs();
        var temporaryZones = registry.GetTemporaryZones();
        Console.WriteLine(JsonSerializer.Serialize(
          new NativeSelectorsResult(
            gameId,
            dialogs
              .Select(dialog => new SelectorDialogReport(
                dialog.Title,
                dialog.Items
                  .Select(item => new SelectorItemReport(
                    item.CardId,
                    item.Name,
                    item.CanSelect,
                    item.Position?.X,
                    item.Position?.Y,
                    item.Position?.Width,
                    item.Position?.Height))
                  .ToList()))
              .ToList(),
            temporaryZones
              .Select(zone => new TemporaryZoneReport(
                zone.ZoneName,
                zone.Open,
                zone.IsVisible,
                zone.Items
                  .Select(item => new SelectorItemReport(
                    item.CardId,
                    item.Name,
                    item.CanSelect,
                    item.Position?.X,
                    item.Position?.Y,
                    item.Position?.Width,
                    item.Position?.Height))
                  .ToList()))
              .ToList(),
            registry.LastDialogScenesTried,
            registry.LastDialogScenesSeen,
            registry.LastDialogDialogsSeen),
          AgentJson.Options));
        return 0;
      }

      var cards = new List<CardSelectableReport>();

      foreach (int id in CardIds(game, cardIds))
      {
        CardSelectable selectable = registry.GetSelectable(id);

        // Position is resolved only when it was explicitly asked for. It is
        // the one read that needs the client's UI thread, and in a report
        // about click state it is not what is being measured.
        ScreenPoint? position = includePosition
          ? registry.GetPosition(id)
          : null;

        // Actions are read live from the card's own view model. The registry's
        // tracked list is event-fed with no initial load, so in a short-lived
        // process it is legitimately empty and would report every card as
        // having no actions.
        var live = registry.GetLiveActions(id);
        int sourceId = Read(() => game.GetGameCard(id)?.SourceId) ?? 0;
        cards.Add(new CardSelectableReport(
          selectable.CardId,
          selectable.HasActions,
          selectable.IsSelectable,
          selectable.IsTargetable,
          selectable.IsTargeted,
          live.Count,
          live.Select(action => Read(() => action.Name) ?? "?").ToList(),
          registry.GetActions(id, sourceId).Count,
          position?.X,
          position?.Y));
      }

      Console.WriteLine(JsonSerializer.Serialize(
        new NativeActionsResult(
          gameId,
          registry.Version,
          registry.GetRegisteredCardIds().Count,
          cards,
          registry.GetPlayerSelectable()
            .Select(entry => new PlayerReport(
              entry.PlayerIndex,
              entry.IsTargetable,
              entry.IsTargeted,
              entry.IsActive))
            .ToList()),
        AgentJson.Options));
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
  /// scanning the client's managed heap. An explicit id (or comma-separated
  /// list) bypasses the zone walk, which in a fresh process only resolves a
  /// fraction of the model; ids typically come from the exported snapshot.
  /// </summary>
  private static IEnumerable<int> CardIds(Game game, string? requested)
  {
    if (!string.IsNullOrEmpty(requested))
    {
      foreach (string part in requested.Split(','))
      {
        if (int.TryParse(part.Trim(), out int id))
          yield return id;
        else
          throw new ArgumentException(
            $"--card-ids '{part}' is not an integer.");
      }

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

  /// <summary>
  /// Temporary probe: can the batch endpoints serve our duel reads?
  /// Uses card IDs from the exported snapshot (zone enumeration in a fresh
  /// process only resolves a fraction of the model) and compares batch
  /// results against individually-read ground truth.
  /// </summary>
  private static int BatchProbe(Game game)
  {
    List<int> ids;
    try
    {
      string path = AgentStateStore.DefaultPath;
      string full = Path.GetFullPath(path);
      if (!File.Exists(full))
      {
        Console.WriteLine("no state file; cannot source card ids.");
        return 1;
      }

      using var stream = File.OpenRead(full);
      var document = JsonSerializer.Deserialize<JsonDocument>(stream);
      if (document is null)
      {
        Console.WriteLine("state file unreadable; cannot source card ids.");
        return 1;
      }

      ids = document.RootElement.GetProperty("cards").EnumerateArray()
        .Select(card => card.GetProperty("id").GetInt32())
        .Distinct()
        .Take(10)
        .ToList();
      Console.WriteLine($"probing {ids.Count} ids from state file: [{string.Join(",", ids)}]");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"state file unreadable: {ex.GetType().Name}");
      return 1;
    }

    string[] paths =
    [
      "Id", "Name", "Zone.Name", "OwnerIndex", "ControllerIndex",
      "IsAttacking", "IsTapped", "Tag.CardViewModel.HasActions",
      "Tag.CardViewModel.IsSelectable", "Tag.CardViewModel.IsTargetable",
    ];

    foreach (int id in ids)
    {
      dynamic? card;
      try
      {
        card = game.GetGameCard(id);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"card {id}: GetGameCard threw {ex.GetType().Name}");
        continue;
      }

      if (card is null)
      {
        Console.WriteLine($"card {id}: not found");
        continue;
      }

      dynamic raw;
      try
      {
        raw = DLRWrapper.Unbind(card);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"card {id}: unbind threw {ex.GetType().Name}");
        continue;
      }

      if (raw is not DynamicRemoteObject dro)
      {
        Console.WriteLine($"card {id}: unbound is {raw?.GetType()?.FullName}, not a DRO");
        continue;
      }

      try
      {
        dynamic response = RemoteClient.client.Communicator
          .GetBatchMembers(
            dro.__ro.RemoteToken,
            dro.__type?.FullName ?? "Unknown",
            string.Join("|", paths));
        var cells = new List<string>();
        for (int col = 0; col < ((IEnumerable<object>)response.Schema).Cast<object>().Count(); col++)
          cells.Add(Convert.ToString(response.Values[col]) ?? "null");
        Console.WriteLine($"card {id}: [{string.Join(" | ", cells)}]");
      }
      catch (Exception ex)
      {
        Console.WriteLine($"card {id}: batch threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
      }
    }

    return 0;
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

  private static string? OptionalValue(string[] args, string name)
  {
    int index = Array.IndexOf(args, name);
    if (index < 0) return null;

    if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
      throw new ArgumentException($"{name} requires a value.");

    return args[index + 1];
  }

  private sealed record PlayerReport(
    int PlayerIndex,
    bool IsTargetable,
    bool IsTargeted,
    bool IsActive);

  private sealed record SelectorItemReport(
    int CardId,
    string? Name,
    bool CanSelect,
    int? ScreenX,
    int? ScreenY,
    int? Width,
    int? Height);

  private sealed record SelectorDialogReport(
    string Title,
    IReadOnlyList<SelectorItemReport> Items);

  private sealed record TemporaryZoneReport(
    string ZoneName,
    bool Open,
    bool IsVisible,
    IReadOnlyList<SelectorItemReport> Items);

  private sealed record NativeSelectorsResult(
    int GameId,
    IReadOnlyList<SelectorDialogReport> Dialogs,
    IReadOnlyList<TemporaryZoneReport> TemporaryZones,
    int ScenesTried,
    int ScenesSeen,
    int DialogsSeen);

  private sealed record NativeActionsResult(
    int GameId,
    long RegistryVersion,
    int RegisteredCardCount,
    IReadOnlyList<CardSelectableReport> Cards,
    IReadOnlyList<PlayerReport> Players)
  {
    /// <summary>
    /// True when this process attached after the actions were set, so its
    /// tracked registry is legitimately empty. A diagnostic that reported
    /// counts from it would claim every card has no actions.
    /// </summary>
    public bool RegistryIsCold => RegisteredCardCount == 0;
  }

  /// <param name="ScreenX">
  /// Only present with <c>--with-position</c>. Resolving a position costs a
  /// call onto the client's UI thread, so it is not part of a click-state
  /// report by default.
  /// </param>
  private sealed record CardSelectableReport(
    int CardId,
    bool HasActions,
    bool IsSelectable,
    bool IsTargetable,
    bool IsTargeted,
    int LiveActionCount,
    IReadOnlyList<string> LiveActionNames,
    int TrackedActionCount,
    int? ScreenX,
    int? ScreenY);
}
