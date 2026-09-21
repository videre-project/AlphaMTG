/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using AlphaMTG.MTGOAgent;


namespace AlphaMTG;

internal static class Program
{
  [STAThread]
  public static async Task Main()
  {
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

    using var gameStateExporter = new GameStateExporter(logger);
    gameStateExporter.Start();

    logger.LogInformation(
      "MTGO client started. Waiting for shutdown."
    );

    // Keep the process and MTGO client alive until the container is stopped.
    await Task.Delay(Timeout.InfiniteTimeSpan);
    GC.KeepAlive(client);
  }
}
