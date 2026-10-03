/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;


namespace AlphaMTG.MTGOAgent.Router.Tokenizer;

/// <summary>
/// Unigram (SentencePiece-style) tokenizer over the embedded model in
/// <c>tokenizer.json</c>: Metaspace pre-tokenization (whitespace split,
/// every word prefixed with U+2581), Viterbi best-path segmentation by
/// token score, added tokens matched atomically, anything else degrades
/// to [UNK]. Mirrors the tokenizers-library behavior the export was
/// traced against; verify equality on samples, never by inspection.
/// </summary>
internal sealed class UnigramTokenizer : ITokenizer
{
  private readonly Dictionary<string, (int Id, float Score)> _vocab;
  private readonly Dictionary<string, int> _added;
  private readonly int _unk;

  public int ClsId { get; }
  public int SepId { get; }
  public int PadId { get; }

  public UnigramTokenizer(string tokenizerJsonPath)
  {
    using JsonDocument document = JsonDocument.Parse(
      File.ReadAllText(tokenizerJsonPath));
    JsonElement root = document.RootElement;
    JsonElement model = root.GetProperty("model");

    _vocab = new Dictionary<string, (int, float)>(StringComparer.Ordinal);
    int id = 0;
    foreach (JsonElement entry in model.GetProperty("vocab").EnumerateArray())
    {
      string token = entry[0].GetString() ?? string.Empty;
      float score = (float)entry[1].GetDouble();
      _vocab[token] = (id, score);
      id++;
    }

    _added = new Dictionary<string, int>(StringComparer.Ordinal);
    if (root.TryGetProperty("added_tokens", out JsonElement added))
    {
      foreach (JsonElement entry in added.EnumerateArray())
      {
        string? content = entry.GetProperty("content").GetString();
        if (content is not null)
          _added[content] = entry.GetProperty("id").GetInt32();
      }
    }

    _unk = GetId("[UNK]");
    ClsId = GetId("[CLS]");
    SepId = GetId("[SEP]");
    PadId = GetId("[PAD]");
  }

  public IReadOnlyList<int> Encode(string text)
  {
    var ids = new List<int>();
    int position = 0;
    while (position < text.Length)
    {
      string? hit = LongestAddedToken(text, position);
      if (hit is not null)
      {
        ids.Add(_added[hit]);
        position += hit.Length;
        continue;
      }
      int next = NextAddedToken(text, position);
      string word = text[position..(next < 0 ? text.Length : next)];
      ids.AddRange(EncodeWord(word));
      position = next < 0 ? text.Length : next;
    }
    return ids;
  }

  private string? LongestAddedToken(string text, int position)
  {
    string? best = null;
    foreach (string token in _added.Keys)
    {
      if (token.Length > (best?.Length ?? 0)
        && text.AsSpan(position).StartsWith(token, StringComparison.Ordinal))
        best = token;
    }
    return best;
  }

  private int NextAddedToken(string text, int position)
  {
    int best = -1;
    foreach (string token in _added.Keys)
    {
      int found = text.IndexOf(token, position, StringComparison.Ordinal);
      if (found >= 0 && (best < 0 || found < best))
        best = found;
    }
    return best;
  }

  private List<int> EncodeWord(string word)
  {
    // Mirrors the tokenizers-library path piece-for-piece: whitespace
    // split, every word prefixed with U+2581, Unigram Viterbi. No
    // lowercasing here: text words arrive pre-lowered, schema pieces
    // keep their case.
    var ids = new List<int>();
    foreach (string piece in word.Split(
      (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
    {
      ids.AddRange(Viterbi("▁" + piece));
    }
    return ids;
  }

  private List<int> Viterbi(string word)
  {
    // Best log-score path ending at each byte offset; backtrack the win.
    int n = word.Length;
    var best = new double[n + 1];
    var back = new int[n + 1];
    var backId = new int[n + 1];
    for (int i = 1; i <= n; i++) best[i] = double.NegativeInfinity;
    for (int end = 1; end <= n; end++)
    {
      for (int start = 0; start < end; start++)
      {
        if (double.IsNegativeInfinity(best[start])) continue;
        if (!_vocab.TryGetValue(word[start..end], out var entry)) continue;
        double score = best[start] + entry.Score;
        if (score > best[end])
        {
          best[end] = score;
          back[end] = start;
          backId[end] = entry.Id;
        }
      }
    }
    if (double.IsNegativeInfinity(best[n]))
      return new List<int> { _unk };
    var ids = new List<int>();
    for (int at = n; at > 0; at = back[at])
      ids.Add(backId[at]);
    ids.Reverse();
    return ids;
  }

  private int GetId(string token) =>
    _added.TryGetValue(token, out int id) ? id
    : _vocab.TryGetValue(token, out var entry) ? entry.Id
    : throw new InvalidOperationException(
      $"Tokenizer has no '{token}' token.");
}
