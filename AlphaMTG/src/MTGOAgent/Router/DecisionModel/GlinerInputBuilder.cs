/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text;

using AlphaMTG.MTGOAgent.Router.Tokenizer;

namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// Builds one span-extractor input from state plus a question's instructions
/// and criteria:
/// <c>[CLS] ( [P] instructions [DESCRIPTION] label: desc ... ( [L] label ... ) )
/// [SEP_TEXT] Instruction: instructions State: state [SEP]</c>, recording
/// each [L] span position for the gather. Including the instruction in the
/// classified text improved the recorded AlphaMTG goal-to-action cases.
/// </summary>
internal static class GlinerInputBuilder
{
  public sealed record BuiltInput(
    long[] Ids,
    long[] Mask,
    long[] LabelPositions,
    IReadOnlyList<string> LabelIds);

  public static BuiltInput Build(
    ITokenizer tokenizer,
    string instructions,
    string state,
    IReadOnlyList<DecisionCandidate> candidates,
    int maxLength)
  {
    // Schema pieces are tokenized whole, exactly as the transform builds
    // them: the instructions plus all descriptions form one piece, labels stay
    // bare. Text is lowercased word-wise upstream of tokenization.
    var block = new StringBuilder(instructions);
    foreach (DecisionCandidate candidate in candidates)
    {
      if (!string.IsNullOrEmpty(candidate.Description))
        block.Append($" [DESCRIPTION] {candidate.Label}: {candidate.Description}");
    }
    var pieces = new List<string> { "(", "[P]", block.ToString(), "(" };
    foreach (DecisionCandidate candidate in candidates)
    {
      pieces.Add("[L]");
      pieces.Add(candidate.Label);
    }
    pieces.Add(")");
    pieces.Add(")");
    pieces.Add("[SEP_TEXT]");

    var ids = new List<int>();
    var labelPositions = new List<long>();
    var labelIds = new List<string>();
    int candidateIndex = 0;
    foreach (string piece in pieces)
    {
      if (piece == "[L]" && candidateIndex < candidates.Count)
      {
        ids.AddRange(tokenizer.Encode(piece));
        labelPositions.Add(ids.Count - 1);
        labelIds.Add(candidates[candidateIndex].Id);
        candidateIndex++;
        continue;
      }
      ids.AddRange(tokenizer.Encode(piece));
    }
    // The local classifier uses the text as evidence. Put the caller's
    // instruction there so it can compare the requested goal with the state.
    // This is per question because a SystemOne request may contain several
    // questions against the same shared state.
    string decisionText = $"Instruction: {instructions}\nState: {state}";
    foreach (string word in WordSplitter.Split(decisionText))
      ids.AddRange(tokenizer.Encode(word));

    if (ids.Count > maxLength)
    {
      throw new ArgumentException(
        $"Decision input is {ids.Count} tokens, over the {maxLength} limit.");
    }

    return new BuiltInput(
      ids.Select(id => (long)id).ToArray(),
      ids.Select(_ => 1L).ToArray(),
      labelPositions.ToArray(),
      labelIds);
  }
}
