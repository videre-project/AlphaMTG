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
    if (args.Length > 0)
    {
      switch (args[0])
      {
        case "uia": return UIACommand.Run(args[1..]);
        case "native-actions": return NativeActionsCommand.Run(args[1..]);
        case "type-members": return TypeMembersCommand.Run(args[1..]);
        default:
          Console.Error.WriteLine($"Unknown command '{args[0]}'.\n{Usage}");
          return 2;
      }
    }

    if (!AgentInstanceLock.TryAcquire(
          AgentInstanceLock.DefaultPath,
          out AgentInstanceLock? acquiredLock,
          out string? holder)
        || acquiredLock is null)
    {
      Console.Error.WriteLine(
        "Could not lock the AlphaMTG game-state files "
          + $"({AgentInstanceLock.DefaultPath}: {holder}). "
          + "Choose a different ALPHAMTG_GAME_STATE_PATH to write a separate "
          + "state file. Diagnostic commands such as 'uia' and "
          + "'native-actions' are unaffected and may still be run.");
      return 3;
    }

    using AgentInstanceLock instanceLock = acquiredLock;
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

  private const string Usage =
    "Usage: AlphaMTG [command]\n"
      + "\n"
      + "  (no command)  Run the agent. Only one instance may run at a time.\n"
      + "  uia           Drive MTGO through UI Automation.\n"
      + "                  AlphaMTG uia dump-tree [--raw] [--find-name TEXT]\n"
      + "                  AlphaMTG uia select ...\n"
      + "  type-members   List the members a remote type exposes.\n"
      + "                  AlphaMTG type-members --type NAME [--filter TEXT]\n"
      + "  native-actions\n"
      + "                Report what MTGO will accept a click on.\n"
      + "                  AlphaMTG native-actions --game-id ID [--card-id ID]\n"
      + "                                    [--with-position]\n";

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
