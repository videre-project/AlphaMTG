/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// One decidable option.
/// </summary>
/// <param name="Id">The action id from the snapshot. Round-trips untouched.</param>
/// <param name="Label">The short span scored in the [L] list. Usually the id.</param>
/// <param name="Description">The meaning, scored in [DESCRIPTION]. Optional.</param>
internal sealed record DecisionCandidate(
  string Id,
  string Label,
  string? Description = null);
