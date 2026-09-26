/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using AlphaMTG.MTGOAgent;
using AlphaMTG.MTGOAgent.Diagnostics;
using AlphaMTG.MTGOAgent.UIA;


namespace AlphaMTG;

internal static class Program
{
  [STAThread]
  public static async Task<int> Main(string[] args)
  {
    if (args.FirstOrDefault() == "native-actions")
      return NativeActionsCommand.Run(args[1..]);

    if (args.FirstOrDefault() == "uia")
      return UIACommand.Run(args[1..]);

    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    {
      builder
        .AddSimpleConsole(options =>
        {
          options.SingleLine = true;
          options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        })
        .SetMinimumLevel(LogLevel.Information);
    });

    ILogger logger = loggerFactory.CreateLogger("AlphaMTG");
    logger.LogInformation("Starting AlphaMTG.");

    var client = await BotClient.StartAsync(
      loggerFactory,
      logger,
      logOn: true
    );

    using var gameStateExporter = new GameStateExporter(
      logger,
      client.CurrentUser.Id
    );
    gameStateExporter.Start();

    using var nativeActionTest = CreateNativeActionTestController(logger);
    nativeActionTest?.Start();

    logger.LogInformation(
      "MTGO client started. Waiting for shutdown."
    );

    // Keep the process and MTGO client alive until the container is stopped.
    await Task.Delay(Timeout.InfiniteTimeSpan);
    GC.KeepAlive(client);
    return 0;
  }

  private static NativeActionTestController? CreateNativeActionTestController(
    ILogger logger)
  {
    string? enabled = Environment.GetEnvironmentVariable(
      "ALPHAMTG_ENABLE_NATIVE_ACTION_TEST");
    return string.Equals(enabled, "1", StringComparison.Ordinal)
        || string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(enabled, "yes", StringComparison.OrdinalIgnoreCase)
      ? new NativeActionTestController(logger)
      : null;
  }
}
