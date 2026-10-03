/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// Loads a decision model from <c>ALPHAMTG_ROUTER_WEIGHTS</c> when configured.
/// </summary>
internal static class DecisionModelResolver
{
  public static IDecisionModel Resolve()
  {
    string? directory =
      Environment.GetEnvironmentVariable("ALPHAMTG_ROUTER_WEIGHTS");
    if (string.IsNullOrWhiteSpace(directory))
    {
      return new NullDecisionModel();
    }
    return new OnnxDecisionModel(directory);
  }

  private sealed class NullDecisionModel : IDecisionModel
  {
    public void Dispose()
    {
      // Nothing loaded.
    }

    public IReadOnlyDictionary<string, DecisionAnswer> Decide(
      string state, IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
      throw new InvalidOperationException(
        "No decision model configured: set ALPHAMTG_ROUTER_WEIGHTS to a "
        + "weights directory containing router.json.");
    }
  }
}
