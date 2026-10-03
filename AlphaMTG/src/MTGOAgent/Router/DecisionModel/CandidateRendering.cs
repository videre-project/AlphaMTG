/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text;


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// Renders a historical snapshot into the state text and per-action criteria
/// used by replay. Pure function of the snapshot. The local model has a 512
/// token limit per question, so this stays terse by design.
/// </summary>
internal static class CandidateRendering
{
  public static string RenderState(AgentState snapshot)
  {
    var text = new StringBuilder();
    text.Append($"Turn {snapshot.Turn} {snapshot.Phase}. ");
    foreach (var player in snapshot.Players)
      text.Append($"{player.Name} at {player.Life}. ");
    text.Append($"Prompt: {snapshot.Prompt.Text} ");
    foreach (var card in snapshot.Cards)
      text.Append($"{card.Name} ({card.Zone}). ");
    return text.ToString();
  }

  public static IReadOnlyList<DecisionCandidate> RenderCandidates(
    AgentState snapshot)
  {
    // Labels are bare action ids; meaning goes in descriptions. That is
    // the export's contract: [L] spans are scored, [DESCRIPTION] grounds
    // them.
    return snapshot.Actions
      .Select(action =>
      {
        string subject = action.Source.CardId is int cardId
          ? snapshot.Cards.FirstOrDefault(card => card.Id == cardId)?.Name
            ?? $"card {cardId}"
          : string.Empty;
        string meaning = string.IsNullOrEmpty(subject)
          ? $"{action.Type}: {action.Name}"
          : $"{action.Type} {subject}: {action.Name}";
        return new DecisionCandidate(action.Id.ToString(), action.Id.ToString(), meaning.Trim());
      })
      .ToList();
  }
}
