/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;
using System.Reflection;

using Microsoft.Extensions.Logging;

using MTGOSDK.Core.Remoting;


namespace AlphaMTG.MTGOAgent.Diagnostics;

/// <summary>
/// Lists the members the client actually exposes on a remote type.
/// </summary>
/// <remarks>
/// Hook names must match a real method or event accessor exactly, so this
/// prints what is there rather than what the decompiled source suggests.
/// </remarks>
internal static class TypeMembersCommand
{
  public static int Run(string[] args)
  {
    try
    {
      string typeName = RequiredValue(args, "--type");
      string? filter = OptionalValue(args, "--filter");

      using ILoggerFactory loggerFactory = LoggerFactory.Create(
        builder => builder.AddSimpleConsole(options => options.SingleLine = true));
      using Process? process = RemoteClient.MTGOProcess()
        ?? throw new InvalidOperationException(
          "No running MTGO process was found. Start AlphaMTG first.");
      using var client = new MTGOSDK.API.Client(
        new MTGOSDK.API.ClientOptions
        {
          CreateProcess = false,
          CloseOnExit = false
        },
        loggerFactory: loggerFactory,
        process: process);

      Type type = RemoteClient.GetInstanceType(typeName);
      Console.WriteLine($"type: {type.FullName}");

      var members = type
        .GetMethods((BindingFlags) 0xffff)
        .Where(method => filter is null
          || method.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .Select(method => method.Name)
        .Distinct()
        .OrderBy(name => name, StringComparer.Ordinal);

      foreach (string name in members)
        Console.WriteLine(name);

      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }

  private static string RequiredValue(string[] args, string name) =>
    OptionalValue(args, name)
      ?? throw new ArgumentException($"{name} is required.");

  private static string? OptionalValue(string[] args, string name)
  {
    int index = Array.IndexOf(args, name);
    if (index < 0)
      return null;
    if (index + 1 >= args.Length)
      throw new ArgumentException($"{name} requires a value.");
    return args[index + 1];
  }
}
