/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json;
using System.Text.Json.Serialization;

using MTGOSDK.API.Play.Games.Controls;


namespace AlphaMTG.MTGOAgent;

internal sealed record AgentAction(
  int Id,
  string Type,
  string Name,
  ActionSource Source,
  IReadOnlyList<TargetSetState>? Targets = null,
  NumberRange? Number = null,
  IReadOnlyList<ActionChoice>? Choices = null,
  IReadOnlyList<int>? Order = null,
  IReadOnlyList<DistributionState>? Distribution = null,
  NumberRange? DistributionTotal = null
);

internal sealed record ActionSource(
  string Type,
  int? CardId = null,
  PromptControlId? ControlId = null,
  long? ClientRevision = null,
  string? ButtonType = null,
  string? Color = null);

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
