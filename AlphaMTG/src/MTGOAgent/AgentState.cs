/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;


namespace AlphaMTG.MTGOAgent;

internal static class AgentJson
{
  public static readonly JsonSerializerOptions Options = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
  };
}

internal static class AgentStateStore
{
  public static string DefaultPath =>
    Environment.GetEnvironmentVariable("ALPHAMTG_GAME_STATE_PATH")
    ?? Path.Combine("AlphaMTG", "mtgo-game.json");

  public static AgentState Read(string path)
  {
    return JsonSerializer.Deserialize<AgentState>(
      File.ReadAllText(path),
      AgentJson.Options
    ) ?? throw new InvalidOperationException("The game-state file is empty.");
  }

  public static void Write(string path, AgentState state)
  {
    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    var temporaryPath = fullPath + ".tmp";
    File.WriteAllText(
      temporaryPath,
      JsonSerializer.Serialize(state, AgentJson.Options)
    );
    File.Move(temporaryPath, fullPath, overwrite: true);
  }
}

internal sealed record AgentState(
  DateTimeOffset CapturedAtUtc,
  int GameId,
  int Nonce,
  uint StateTimestamp,
  int Turn,
  string Phase,
  PromptState Prompt,
  IReadOnlyList<PlayerState> Players,
  IReadOnlyList<CardState> Cards,
  IReadOnlyList<AgentAction> Actions
);

internal sealed record PromptState(string Text, byte PromptedPlayer);

internal sealed record PlayerState(
  int Index,
  int UserId,
  string Name,
  int Life,
  bool HasPriority
);

internal sealed record CardState(
  int Id,
  int SourceId,
  int Ctn,
  string Name,
  string? Zone,
  int OwnerIndex,
  int ControllerIndex,
  int? ZoneIndex,
  int? NameOrdinal
);

internal sealed record AgentAction(
  int Id,
  string Type,
  string Name,
  ActionSource Source,
  IReadOnlyList<TargetSetState>? Targets = null,
  NumberRange? Number = null,
  IReadOnlyList<ActionChoice>? Choices = null,
  IReadOnlyList<int>? Order = null,
  IReadOnlyList<DistributionState>? Distribution = null
);

internal sealed record ActionSource(string Type, int? CardId = null);

internal sealed record TargetSetState(
  int Index,
  string? Description,
  int Minimum,
  int Maximum,
  IReadOnlyList<GameObjectState> Legal,
  IReadOnlyList<GameObjectState> Selected
);

internal sealed record GameObjectState(string Type, int Id, string Name);
internal sealed record NumberRange(int Minimum, int Maximum, int Initial);
internal sealed record ActionChoice(int Id, string Name);
internal sealed record DistributionState(
  GameObjectState Target,
  int Minimum,
  int Maximum,
  int Value
);
