/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.RegularExpressions;


namespace AlphaMTG.MTGOAgent.Router.Tokenizer;

/// <summary>
/// Splits evidence into the lowercase words and punctuation used by the traced export.
/// URLs, email addresses, mentions, and words with internal dashes stay together.
/// Schema labels bypass this splitter so their original case is preserved.
/// </summary>
internal static partial class WordSplitter
{
  [GeneratedRegex(
    @"(?:https?://[^\s]+|www\.[^\s]+)|[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}|@[a-z0-9_]+|\w+(?:[-_]\w+)*|\S",
    RegexOptions.IgnoreCase)]
  private static partial Regex Pattern();

  public static IReadOnlyList<string> Split(string text)
  {
    return Pattern().Matches(text)
      .Select(match => match.Value.ToLowerInvariant())
      .ToList();
  }
}
