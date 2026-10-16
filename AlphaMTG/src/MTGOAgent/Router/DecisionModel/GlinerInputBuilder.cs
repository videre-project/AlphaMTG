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
/// <c>( [P] instructions [DESCRIPTION] label: desc ... ( [L] label ... ) )
/// [SEP_TEXT] Instruction: instructions State: state</c>.
/// Records each [L] position for the model's label gather.
/// </summary>
internal static class GlinerInputBuilder
{
  public sealed record BuiltInput(
    long[] Ids,
    long[] Mask,
    long[] LabelPositions);

  public static BuiltInput Build(
    UnigramTokenizer tokenizer,
    string instructions,
    string state,
    IReadOnlyList<DecisionCandidate> candidates,
    int maxLength)
  {
    // Preserve schema pieces as whole strings. Only the evidence text below
    // passes through the word splitter.
    var block = new StringBuilder(instructions);
    foreach (DecisionCandidate candidate in candidates)
    {
      if (!string.IsNullOrEmpty(candidate.Description))
        block.Append($" [DESCRIPTION] {candidate.Label}: {candidate.Description}");
    }
    var ids = new List<int>();
    var labelPositions = new List<long>();
    ids.AddRange(tokenizer.Encode("("));
    ids.AddRange(tokenizer.Encode("[P]"));
    ids.AddRange(tokenizer.Encode(block.ToString()));
    ids.AddRange(tokenizer.Encode("("));
    foreach (DecisionCandidate candidate in candidates)
    {
      ids.AddRange(tokenizer.Encode("[L]"));
      labelPositions.Add(ids.Count - 1);
      ids.AddRange(tokenizer.Encode(candidate.Label));
    }
    ids.AddRange(tokenizer.Encode(")"));
    ids.AddRange(tokenizer.Encode(")"));
    ids.AddRange(tokenizer.Encode("[SEP_TEXT]"));

    // Include the requested instruction in the evidence for this question.
    // Several questions can use the same state while asking for different decisions.
    string decisionText = $"Instruction: {instructions}\nState: {state}";
    foreach (string word in WordSplitter.Split(decisionText))
      ids.AddRange(tokenizer.Encode(word));

    if (ids.Count > maxLength)
    {
      throw new ArgumentOutOfRangeException(nameof(maxLength),
        $"Decision input is {ids.Count} tokens, over the {maxLength} limit.");
    }

    return new BuiltInput(
      ids.Select(id => (long)id).ToArray(),
      Enumerable.Repeat(1L, ids.Count).ToArray(),
      labelPositions.ToArray());
  }
}
