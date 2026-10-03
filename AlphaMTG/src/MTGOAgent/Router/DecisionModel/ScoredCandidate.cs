/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text;


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// A candidate with its model score. Higher means preferred.
/// </summary>
internal sealed record ScoredCandidate(string Id, float Score);
