/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json;
using System.Text.Json.Serialization;


namespace AlphaMTG.MTGOAgent;

internal sealed record AgentState(
  DateTimeOffset CapturedAtUtc,
  int GameId,
  int Nonce,
  int Turn,
  string Phase,
  PromptState Prompt,
  IReadOnlyList<PlayerState> Players,
  IReadOnlyList<CardState> Cards,
  IReadOnlyList<AgentAction> Actions
);

internal sealed record PromptState(string Text, string? PromptedPlayer);

internal sealed record PlayerState(
  int Index,
  string Name,
  int Life,
  bool HasPriority,
  // What MTGO will accept a click on for this player right now. Written by
  // the scene's own targetability pass and gated by InteractionState.Mode, so
  // these already account for whether a target is being asked for.
  //
  // No screen position: a player view model holds no reference to its own
  // view, so unlike a card there is no view-model path to resolve where the mat is
  // drawn. Player clicks go through the UI Automation tree.
  bool Targetable,
  bool Targeted,
  bool Active
);

internal sealed record CardState(
  int Id,
  string Name,
  string? Zone,
  int OwnerIndex
);
