/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;

using AlphaMTG.MTGOAgent.Router.DecisionModel;


namespace AlphaMTG.MTGOAgent.Router;

/// <summary>
/// Replays a decision log through a decision model. The tick is rebuilt
/// into the same <see cref="AgentState"/> the executor sees and rendered
/// by the same <see cref="CandidateRendering"/>, so a scored log measures
/// the production input rather than a replay-only approximation of it.
/// Ticks the model could not score are failures, not skips: a log that
/// cannot be reproduced is a finding.
/// </summary>
internal static class DecisionLogReplay
{
  public static IReadOnlyList<ReplayOutcome> Run(
    string logPath, IDecisionModel model)
  {
    var outcomes = new List<ReplayOutcome>();
    int tick = 0;
    foreach (string line in File.ReadLines(logPath))
    {
      if (string.IsNullOrWhiteSpace(line)) continue;
      TickRecord? record = JsonSerializer.Deserialize<TickRecord>(line);
      if (record is null) continue;

      AgentState state = RebuiltState(record);
      IReadOnlyList<DecisionCandidate> candidates =
        CandidateRendering.RenderCandidates(state);
      IReadOnlyList<ScoredCandidate> scored = candidates.Count == 0
        ? Array.Empty<ScoredCandidate>()
        : model.Score(CandidateRendering.RenderState(state), candidates);

      int chosen = scored.Count == 0 ? -1 : int.Parse(scored[0].Id);
      outcomes.Add(new ReplayOutcome(
        tick++,
        record.Phase,
        record.Prompt,
        record.Executed.ActionId,
        chosen,
        chosen == record.Executed.ActionId,
        scored.Count < 2 ? 0d : scored[0].Score - scored[1].Score));
    }
    return outcomes;
  }

  /// <summary>
  /// Rebuilds the snapshot a tick was recorded from. Fields the log does
  /// not carry stay empty; the renderer reads none of them.
  /// </summary>
  private static AgentState RebuiltState(TickRecord record) =>
    new(
      record.At,
      record.GameId,
      record.NonceBefore,
      record.Turn,
      record.Phase,
      new PromptState(record.Prompt, null),
      record.Players
        .Select(player => new PlayerState(
          player.Index, player.Name, player.Life,
          false, false, false, false))
        .ToList(),
      record.Cards
        .Select(card => new CardState(card.Id, card.Name, card.Zone, 0))
        .ToList(),
      record.Offered
        .Select(action => new AgentAction(
          action.Id, action.Type, action.Name,
          new ActionSource(action.Type, action.CardId)))
        .ToList());
}
