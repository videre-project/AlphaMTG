/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.Core.Security;


namespace AlphaMTG.MTGOAgent;

internal static class BotClient
{
  public static async Task<Client> StartAsync(
    ILoggerFactory loggerFactory,
    ILogger logger,
    bool logOn
  )
  {
    while (!await Client.IsOnline())
    {
      logger.LogInformation("MTGO servers are currently offline. Waiting...");
      await Task.Delay(TimeSpan.FromMinutes(30));
    }

    var client = new Client(
      new ClientOptions
      {
        CreateProcess = true,
        CloseOnExit = true,
        AcceptEULAPrompt = true
      },
      loggerFactory: loggerFactory
    );

    if (logOn && !client.IsConnected)
    {
      DotEnv.LoadFile();
      var logOnTask = client.LogOn(
        username: DotEnv.Get("USERNAME"),
        password: DotEnv.Get("PASSWORD")
      );
      while (!logOnTask.IsCompleted && !IsSessionReady(client))
        await Task.Delay(250);

      if (logOnTask.IsCompleted)
        await logOnTask;
      else
        _ = ObserveLogOn(logOnTask, logger);

      logger.LogInformation("Connected as {Username}.", client.CurrentUser.Name);
    }

    return client;
  }

  private static bool IsSessionReady(Client client)
  {
    try
    {
      return client.IsConnected || client.IsLoggedIn;
    }
    catch
    {
      return false;
    }
  }

  private static async Task ObserveLogOn(Task logOnTask, ILogger logger)
  {
    try
    {
      await logOnTask;
    }
    catch (Exception exception)
    {
      logger.LogWarning(exception, "MTGOSDK login task failed after connecting.");
    }
  }
}
