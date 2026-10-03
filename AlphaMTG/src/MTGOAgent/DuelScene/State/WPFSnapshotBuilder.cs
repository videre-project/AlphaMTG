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

internal sealed class WPFSnapshotBuilder(ILogger logger)
{
  private static readonly TimeSpan s_slowWpfWrite =
    TimeSpan.FromMilliseconds(250);

  internal WPFSnapshot? WriteWPFSnapshot(
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
      var wpfCards = new WPFCardSnapshot[ordered.Count];
      Parallel.For(0, ordered.Count, CardResolver.BatchOptions(ordered.Count), i =>
      {
        wpfCards[i] = ProjectWPFCard(ordered[i]);
      });
      IReadOnlyList<WPFCardSnapshot> cards = wpfCards;
      var document = new WPFSnapshot(
        DateTimeOffset.UtcNow,
        game.Id,
        snapshot.Nonce,
        prompt.Nonce,
        Try<string?>(() => prompt.Text),
        registry?.Version ?? 0,
        GameProcessor.ForGame(game).InteractionState.Mode.ToString(),
        GameProcessor.ForGame(game).InteractionState.PromptBoxText,
        cards);

      var elapsed = Stopwatch.GetElapsedTime(started);
      if (elapsed > s_slowWpfWrite)
      {
        logger.LogWarning(
          "Native UI snapshot for MTGO game {GameId} took {ElapsedMs} ms "
            + "for {CardCount} cards.",
          game.Id,
          (int)elapsed.TotalMilliseconds,
          cards.Count);
      }
      return document;
    }
    catch (Exception exception)
    {
      logger.LogWarning(
        exception,
        "Failed to write native UI snapshot for MTGO game {GameId}.",
        game.Id);
      return null;
    }
  }

  private static WPFCardSnapshot ProjectWPFCard(ResolvedCard card)
  {
    return new WPFCardSnapshot(
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
        .Select((action, index) => ActionProjection.ProjectWPFAction(action, index))
        .ToList());
  }
}
