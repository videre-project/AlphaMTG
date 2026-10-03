/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Controllers.Models;

internal sealed record ExecuteActionRequest(
  int ActionId,
  int Nonce,
  int? GameId = null,
  int? TargetSetId = null,
  int? TargetId = null,
  int? ChoiceId = null,
  int? Number = null,
  IReadOnlyList<int>? Order = null,
  IReadOnlyList<DistributionInputRequest>? Distributions = null,
  string? Wish = null
);
