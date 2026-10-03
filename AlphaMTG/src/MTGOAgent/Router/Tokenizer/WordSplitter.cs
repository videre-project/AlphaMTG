/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.RegularExpressions;


namespace AlphaMTG.MTGOAgent.Router.Tokenizer;

/// <summary>
/// Word-boundary splitter used before subword tokenization of state text.
/// URLs, emails, and mentions stay whole; word characters (with internal
/// dashes/underscores) stay whole; every other non-space character stands
/// alone. Values are lower-cased; schema pieces never pass through here.
/// Mirrors the whitespace splitter the export was traced against.
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
