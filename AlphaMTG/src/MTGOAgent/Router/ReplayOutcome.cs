/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

namespace AlphaMTG.MTGOAgent.Router;

/// <summary>
/// One replayed tick: what the model ranked first against what the tick
/// actually executed.
/// </summary>
/// <param name="Tick">Line number in the log, zero-based.</param>
/// <param name="Phase">Game phase the tick was recorded in.</param>
/// <param name="Prompt">Prompt text shown when the tick was recorded.</param>
/// <param name="ExecutedActionId">Action id the tick executed.</param>
/// <param name="ModelActionId">Action id the model ranked first.</param>
/// <param name="Correct">Whether the two agree.</param>
/// <param name="Confidence">Top probability minus runner-up; one for a lone candidate.</param>
internal sealed record ReplayOutcome(
  int Tick,
  string Phase,
  string Prompt,
  int ExecutedActionId,
  int ModelActionId,
  bool Correct,
  double Confidence);
