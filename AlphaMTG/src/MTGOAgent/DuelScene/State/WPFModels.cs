/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json;
using System.Text.Json.Serialization;


namespace AlphaMTG.MTGOAgent;

// This is intentionally separate from AgentState. It describes the live
// action view from the SDK card-action registry, and is refreshed when a local
// action collection changes without waiting for another GameStateSnapshot.
internal sealed record WPFSnapshot(
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
  IReadOnlyList<WPFCardSnapshot> Cards
);

internal sealed record WPFCardSnapshot(
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
  IReadOnlyList<WPFActionSnapshot> Actions
);

internal sealed record WPFActionSnapshot(
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
