/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json.Serialization;


namespace AlphaMTG.MTGOAgent.Controllers.Models;

public sealed record ExecuteActionRequest(
  [property: JsonRequired] int ActionId,
  [property: JsonRequired] int Nonce,
  int? GameId = null,
  int? TargetSetId = null,
  int? TargetId = null,
  int? ChoiceId = null,
  int? Number = null,
  IReadOnlyList<int>? Order = null,
  IReadOnlyList<DistributionInput>? Distributions = null,
  string? Wish = null
);

/// <summary>
/// Assigns a requested amount to an SDK distribution target.
/// </summary>
public sealed record DistributionInput(int TargetId, int Value);
