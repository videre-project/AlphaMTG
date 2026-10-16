/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// Answers named, instruction-conditioned questions against one shared state.
/// A backend owns a loaded session and must be disposed.
/// </summary>
internal interface IDecisionModel : IDisposable
{
  IReadOnlyDictionary<string, DecisionAnswer> Decide(
    string state, IReadOnlyDictionary<string, DecisionQuestion> questions);
}
