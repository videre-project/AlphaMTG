/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;


namespace AlphaMTG.MTGOAgent.Router.Tokenizer;

/// <summary>
/// Tokenizer behind an interface: exports differ, the scoring loop must
/// not care. Reads the format, not the model: vocab, scores, and special
/// ids all come from <c>tokenizer.json</c>.
/// </summary>
internal interface ITokenizer
{
  int ClsId { get; }
  int SepId { get; }
  int PadId { get; }
  IReadOnlyList<int> Encode(string text);
}
