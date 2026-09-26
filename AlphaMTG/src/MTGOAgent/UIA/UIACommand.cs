/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json;

namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// CLI entry point for driving MTGO through its UI Automation tree.
/// </summary>
/// <remarks>
/// Execution only. The state this consumes is produced natively by
/// <see cref="GameStateExporter"/>; this command reads that state and clicks,
/// and never inspects or decides game rules.
/// </remarks>
internal static class UIACommand
{
  public static int Run(string[] args)
  {
    try
    {
      string command = args.FirstOrDefault() ?? "";
      string statePath = AgentStateStore.DefaultPath;
      if (command == "dump-tree")
        return TreeDumpCommand.Run(args);

      object result;

      switch (command)
      {
        case "execute-action":
        {
          using var executor = new ActionExecutor(statePath);
          var targetSet = OptionalInt(args, "--target-set");
          var targetId = OptionalInt(args, "--target-id");
          var choiceId = OptionalInt(args, "--choice-id");
          if (targetSet.HasValue != targetId.HasValue)
          {
            throw new ArgumentException(
              "--target-set and --target-id must be supplied together.");
          }
          if (targetSet.HasValue && choiceId.HasValue)
          {
            throw new ArgumentException(
              "A target and --choice-id cannot be supplied together.");
          }

          result = executor.Execute(
            RequiredInt(args, "--game-id"),
            RequiredInt(args, "--nonce"),
            RequiredInt(args, "--action-id"),
            targetSet.HasValue
              ? new TargetSelection(targetSet.Value, targetId!.Value)
              : null,
            choiceId);
          break;
        }

        case "select":
        {
          using var executor = new ActionExecutor(statePath);
          var cardId = OptionalInt(args, "--card-id");
          var playerName = OptionalValue(args, "--player");
          if (cardId.HasValue && playerName is not null)
          {
            throw new ArgumentException(
              "--card-id and --player cannot be supplied together.");
          }

          result = executor.Select(
            new SelectionTarget(cardId, playerName));
          break;
        }

        case "click-control":
        {
          using var executor = new ActionExecutor(statePath);
          result = executor.ClickControl(
            RequiredValue(args, "--name"),
            0,
            "click-control");
          break;
        }

        default:
          throw new ArgumentException(
            "Use 'uia dump-tree [--automation-id ID | --name-contains TEXT] "
              + "[--depth N] [--properties] [--ancestors] [--raw]', "
              + "'uia dump-tree --find-name TEXT [--raw] [--ancestors]', "
              + "'uia select (--card-id N | --player NAME)', "
              + "'uia execute-action --game-id N --nonce N --action-id N "
              + "[--target-set N --target-id N | --choice-id N]' "
              + "or 'uia click-control --name LABEL'.");
      }

      Console.WriteLine(JsonSerializer.Serialize(result, AgentJson.Options));
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }

  private static string? OptionalValue(string[] args, string name)
  {
    int index = Array.IndexOf(args, name);
    return index < 0 || index + 1 >= args.Length
      ? null
      : args[index + 1];
  }

  private static int RequiredInt(string[] args, string name)
  {
    if (!int.TryParse(RequiredValue(args, name), out int value))
      throw new ArgumentException($"{name} requires an integer value.");

    return value;
  }

  private static string RequiredValue(string[] args, string name)
  {
    int index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length
        || string.IsNullOrWhiteSpace(args[index + 1]))
    {
      throw new ArgumentException($"{name} requires a value.");
    }

    return args[index + 1];
  }

  private static int? OptionalInt(string[] args, string name)
  {
    int index = Array.IndexOf(args, name);
    if (index < 0) return null;

    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value))
      throw new ArgumentException($"{name} requires an integer value.");

    return value;
  }
}
