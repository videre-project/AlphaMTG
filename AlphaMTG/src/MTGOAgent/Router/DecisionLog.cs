/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;


namespace AlphaMTG.MTGOAgent.Router;

/// <summary>
/// Writes one JSONL execution log per game for diagnostics.
/// A logging failure does not fail the requested game action.
/// </summary>
internal sealed class DecisionLog
{
  private readonly object _writeLock = new();
  private static readonly JsonSerializerOptions s_jsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition =
      System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
  };

  public void Record(
    int gameId,
    object state,
    int? nonceAfter,
    object request,
    object? result = null,
    string? note = null)
  {
    try
    {
      var record = new
      {
        at = DateTimeOffset.UtcNow,
        state,
        nonceAfter,
        request,
        result,
        note
      };
      string directory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "logs");
      Directory.CreateDirectory(directory);
      string line = JsonSerializer.Serialize(record, s_jsonOptions);
      lock (_writeLock)
      {
        File.AppendAllText(
          Path.Combine(directory, $"decisions-{gameId}.jsonl"),
          line + "\n");
      }
    }
    catch
    {
      // A failed log write must not turn a dispatched action into a failed request.
    }
  }
}
