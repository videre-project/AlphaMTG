/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// One named outcome in a question's criteria.
/// </summary>
/// <param name="Id">Outcome key, returned unchanged in the answer.</param>
/// <param name="Label">The short span scored in the [L] list. Usually the key.</param>
/// <param name="Description">The meaning, supplied in [DESCRIPTION].</param>
public sealed record DecisionCandidate(
  string Id,
  string Label,
  string? Description = null);
