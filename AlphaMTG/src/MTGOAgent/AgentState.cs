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

  public static void Write<T>(string path, T state)
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
  bool HasPriority,
  // What MTGO will accept a click on for this player right now. Written by
  // the scene's own targetability pass and gated by InteractionState.Mode, so
  // these already account for whether a target is being asked for.
  //
  // No screen position: a player view model holds no reference to its own
  // view, so unlike a card there is no native way to resolve where the mat is
  // drawn. Player clicks go through the UI Automation tree.
  bool Targetable,
  bool Targeted,
  bool Active
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

// This is intentionally separate from AgentState. It describes the live native
// action view from the SDK card-action registry, and is refreshed when a local
// action collection changes without waiting for another GameStateSnapshot.
internal sealed record NativeUiSnapshot(
  DateTimeOffset CapturedAtUtc,
  int GameId,
  int ServerNonce,
  int PromptNonce,
  string? PromptText,
  long RegistryVersion,
  // The duel scene's client-local interaction mode (for example,
  // SelectTargets while MTGO is waiting for a target) and the prompt box
  // text the client set for it. Target selection produces no server snapshot
  // and no game prompt, so without these the targeting state is invisible.
  string InteractionMode,
  string? PromptBoxText,
  IReadOnlyList<NativeCardSnapshot> Cards
);

internal sealed record NativeCardSnapshot(
  int Id,
  int SourceId,
  string? Name,
  string? Zone,
  bool IsAttacking,
  bool IsTapped,
  // What the client will accept a click on right now. This is deliberately
  // separate from Actions: a card can be selectable with an empty action list,
  // which is how MTGO offers a target for selection.
  //
  // Screen position is intentionally absent. It is only needed at the moment
  // of a click, and publishing coordinates in a snapshot invites acting on
  // coordinates that have since gone stale. The registry resolves a position
  // on demand at selection time.
  bool Selectable,
  bool Targetable,
  bool Targeted,
  IReadOnlyList<NativeActionSnapshot> Actions
);

internal sealed record NativeActionSnapshot(
  int Index,
  int ActionFlags,
  string Type,
  string Name,
  string? GroupName,
  string? ActionChoices,
  string? ModeChoiceMapping,
  int TargetCount,
  bool CanBePerformedLocally,
  bool AltMenuAction,
  int AttackVictimId
);

