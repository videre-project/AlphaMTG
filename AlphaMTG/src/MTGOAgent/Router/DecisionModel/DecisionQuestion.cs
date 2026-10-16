/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// One named question. Criteria order is significant for a score rubric.
/// Noul uses the true and false outcomes as its two criteria.
/// </summary>
public sealed record DecisionQuestion(
  string Type,
  string Instructions,
  IReadOnlyList<DecisionCandidate> Criteria);
