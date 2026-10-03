/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;

using AlphaMTG.MTGOAgent.Router.DecisionModel;


namespace AlphaMTG.MTGOAgent.Router;

/// <summary>
/// Append-only JSONL decision log, one file per game under the project
/// logs directory. Written best-effort: logging never fails a game
/// action. Nothing reads it yet; the model loop and fine-tuning will.
/// </summary>
internal sealed class DecisionLog
{
  private readonly object _gate = new();

  public void Record(TickRecord record)
  {
    try
    {
      string directory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "logs");
      Directory.CreateDirectory(directory);
      string line = JsonSerializer.Serialize(record, new JsonSerializerOptions
      {
        DefaultIgnoreCondition =
          System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
      });
      lock (_gate)
      {
        File.AppendAllText(
          Path.Combine(directory, $"decisions-{record.GameId}.jsonl"),
          line + "\n");
      }
    }
    catch
    {
      // Logging is observational. Swallow everything.
    }
  }
}
