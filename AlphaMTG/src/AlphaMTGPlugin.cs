/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;
using System.IO;

using Microsoft.Extensions.DependencyInjection;

using AlphaMTG.MTGOAgent.Router;
using AlphaMTG.MTGOAgent.Router.DecisionModel;
using MTGOTracker.Plugins;


namespace AlphaMTG;

/// <summary>
/// Registers AlphaMTG's decision and action services with Tracker.
/// </summary>
public sealed class AlphaMTGPlugin : ITrackerPlugin
{
  internal static readonly ActivitySource Activities = new("AlphaMTG");

  /// <inheritdoc/>
  public void ConfigureServices(IServiceCollection services, PluginContext context)
  {
    services.AddSingleton(_ => new DecisionLog(Path.Combine(context.DataDirectory, "logs")));
    string? weightsDirectory = context.Configuration["WeightsDirectory"];
    if (!string.IsNullOrWhiteSpace(weightsDirectory))
    {
      string path = Path.GetFullPath(weightsDirectory, context.InstallationDirectory);
      services.AddSingleton<IDecisionModel>(_ => new OnnxDecisionModel(path));
    }
  }
}
