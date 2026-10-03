/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;

using AlphaMTG.MTGOAgent.Router.DecisionModel;


namespace AlphaMTG.MTGOAgent.Router;

/// <summary>
/// One executed tick: what was offered, what was picked, what changed.
/// The fine-tune dataset and the replay log are the same file.
/// </summary>
internal sealed record TickRecord(
  DateTimeOffset At,
  int GameId,
  int NonceBefore,
  int? NonceAfter,
  int Turn,
  string Phase,
  string Prompt,
  IReadOnlyList<PlayerRecord> Players,
  IReadOnlyList<CardRecord> Cards,
  IReadOnlyList<OfferedAction> Offered,
  ExecutedAction Executed,
  IReadOnlyList<ScoredCandidate>? ModelScores,
  string? Note);
