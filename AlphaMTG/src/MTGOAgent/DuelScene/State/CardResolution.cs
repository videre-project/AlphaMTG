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
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Reflection.Serialization;

namespace AlphaMTG.MTGOAgent;

internal sealed class CardResolver(ILogger logger)
{
  private readonly ConcurrentDictionary<int, byte> _batchChecked = new();
  private readonly ConcurrentDictionary<int, byte> _batchBypass = new();
  private readonly ConcurrentDictionary<int, byte> _batchLogged = new();

  /// <summary>
  /// Bounded parallelism for per-card and per-action remote reads.
  /// </summary>
  /// <remarks>
  /// Mirrors the SDK's own batch discipline: one parallel level at a time,
  /// per-item work sequential inside. Callers must never nest one parallel
  /// loop inside another.
  /// </remarks>
  internal static ParallelOptions BatchOptions(int count) =>
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

  internal IReadOnlyList<ResolvedCard> ResolveCards(
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
          Try<int>(() => card.CTN),
          row.Zone,
          row.OwnerIndex,
          Try<int>(() => card.ControllerIndex),
          row.Name ?? Try<string?>(() => card.Name),
          Try<bool>(() => card.IsAttacking),
          Try<bool>(() => card.IsTapped),
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
        Try<int>(() => card.CTN),
        Try<string?>(() => card.Zone?.Name),
        Try<int>(() => card.OwnerIndex),
        Try<int>(() => card.ControllerIndex),
        Try<string?>(() => card.Name),
        Try<bool>(() => card.IsAttacking),
        Try<bool>(() => card.IsTapped),
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
      int userId = Try<int>(() => entry.Value.UserId);
      ownerByUser[userId] = entry.Key;
    }

    var pairs = new List<(GamePlayer Player, int OwnerIndex, CardZone Zone)>();
    for (int i = 0; i < players.Count; i++)
    {
      int userId = Try<int>(() => players[i].UserId);
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
      GameZone? resolved = Try<GameZone?>(() => game.GetGameZone(player, zone));
      if (resolved is null) return;

      string? zoneName = Try<string?>(() => resolved.Name);
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

}

/// <summary>
/// Everything both exported documents need for one card, read once per
/// export.
/// </summary>
internal sealed record ResolvedCard(
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
/// One row of zone-batch data: everything the batch endpoint served.
/// </summary>
internal sealed record BatchedRow(
  string? Zone,
  int OwnerIndex,
  string? Name,
  bool HasActions,
  bool IsSelectable,
  bool IsTargetable,
  bool IsTargeted);
