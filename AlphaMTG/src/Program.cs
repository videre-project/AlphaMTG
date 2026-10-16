/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

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

using MTGOSDK.Core.Diagnostics;
using AlphaMTG.MTGOAgent;
using AlphaMTG.MTGOAgent.Router;
using AlphaMTG.MTGOAgent.Router.DecisionModel;


namespace AlphaMTG;

internal static class Program
{
  [STAThread]
  public static async Task Main(string[] args)
  {
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
        // Discover the agent's internal controllers when building MVC routes.
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
    builder.Services.AddSingleton<DecisionLog>();
    string? weightsDirectory = Environment.GetEnvironmentVariable("ALPHAMTG_ROUTER_WEIGHTS");
    if (!string.IsNullOrWhiteSpace(weightsDirectory))
      builder.Services.AddSingleton<IDecisionModel>(_ => new OnnxDecisionModel(weightsDirectory));

    WebApplication app = builder.Build();
    app.Use(async (context, next) =>
    {
      using var timing = LocalTelemetry.Begin("alpha.http",
        context.Request.Method + " " + context.Request.Path, context.TraceIdentifier);
      await next();
    });
    app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));
    app.MapControllers();
    await app.RunAsync();
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
