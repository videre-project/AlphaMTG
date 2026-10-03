/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using AlphaMTG.MTGOAgent;
using AlphaMTG.MTGOAgent.Router;
using AlphaMTG.MTGOAgent.Router.DecisionModel;


namespace AlphaMTG;

internal static class Program
{
  [STAThread]
  public static async Task Main(string[] args)
  {
    if (args.Length == 2 && args[0] == "--replay")
    {
      await ReplayRunAsync(args[1]);
      return;
    }

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options =>
    {
      options.SingleLine = true;
      options.TimestampFormat = "HH:mm:ss ";
    });
    builder.Logging.SetMinimumLevel(LogLevel.Information);

    builder.Services.AddControllers()
      .AddJsonOptions(options =>
      {
        options.JsonSerializerOptions.DefaultIgnoreCondition =
          JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.PropertyNamingPolicy =
          JsonNamingPolicy.CamelCase;
      })
      .ConfigureApplicationPartManager(manager =>
      {
        // Controllers stay internal like the rest of the agent; discover
        // them explicitly instead of widening the assembly's surface.
        manager.FeatureProviders.Add(new InternalControllerFeatureProvider());
      });

    int port = 8080;
    string? portText = Environment.GetEnvironmentVariable("ALPHAMTG_HTTP_PORT");
    if (!string.IsNullOrWhiteSpace(portText) && int.TryParse(portText, out int parsed))
      port = parsed;
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

    builder.Services.AddSingleton<Server>(services =>
      Server.StartAsync(
        services.GetRequiredService<ILoggerFactory>(),
        services.GetRequiredService<ILogger<Server>>())
        .GetAwaiter().GetResult());

    WebApplication app = builder.Build();
    // Plain HTTP only: no HTTPS redirection or certificates. The old
    // TcpListener transport behaved the same way.
    app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));
    app.MapControllers();
    await app.RunAsync();
  }

  /// <summary>
  /// Scores a decision log against the configured model and prints where
  /// it agrees with what was played. Runs before the web host so no client
  /// or game is started.
  /// </summary>
  private static async Task ReplayRunAsync(string logPath)
  {
    using IDecisionModel model = DecisionModelResolver.Resolve();
    IReadOnlyList<ReplayOutcome> outcomes = DecisionLogReplay.Run(logPath, model);

    int correct = outcomes.Count(outcome => outcome.Correct);
    Console.WriteLine($"{Path.GetFileName(logPath)}: "
      + $"{correct}/{outcomes.Count} ticks match");
    foreach (ReplayOutcome outcome in outcomes)
    {
      Console.WriteLine(
        $"  {outcome.Tick,3}  {(outcome.Correct ? "ok  " : "MISS")}  "
        + $"margin {outcome.Margin,6:F2}  played {outcome.ExecutedActionId}, "
        + $"model {outcome.ModelActionId}  {outcome.Phase}  {outcome.Prompt}");
    }
  }

  /// <summary>
  /// Discovers internal controllers without making them public.
  /// </summary>
  private sealed class InternalControllerFeatureProvider
    : ControllerFeatureProvider
  {
    protected override bool IsController(TypeInfo typeInfo) =>
      typeInfo is { IsClass: true, IsAbstract: false, IsGenericType: false }
      && (typeInfo.Name.EndsWith("Controller", StringComparison.Ordinal)
        || base.IsController(typeInfo));
  }
}
