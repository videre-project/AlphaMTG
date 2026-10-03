/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System;
using System.Text;


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// Scores candidates against a state. Implemented by decision-model
/// backends; the executor loop only speaks this interface, never a model.
/// Disposable because a backend owns a loaded session.
/// </summary>
internal interface IDecisionModel : IDisposable
{
  IReadOnlyList<ScoredCandidate> Score(
    string state, IReadOnlyList<DecisionCandidate> candidates);
}
