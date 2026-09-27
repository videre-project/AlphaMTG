/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json;

using MTGOSDK.API.Play.Games.Processors;

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
          var cardId = OptionalInt(args, "--card-id");
          var playerName = OptionalValue(args, "--player");
          if (cardId.HasValue && playerName is not null)
          {
            throw new ArgumentException(
              "--card-id and --player cannot be supplied together.");
          }

          // A card click is resolved natively, which needs a live client. A
          // player click goes through the automation tree and does not, so the
          // attach is only required when a card was named.
          using NativeSession? session = cardId.HasValue
            ? NativeSession.Attach(statePath)
            : null;

          using var executor = new ActionExecutor(statePath, session?.Registry);
          result = executor.Select(
            new SelectionTarget(cardId, playerName));
          break;
        }

        case "scroll-to":
        {
          // Resolves a verified clickable position, scrolling the zone list
          // when needed, without clicking. This is how scrolling is tested
          // without spending a game action.
          using NativeSession scrollSession = NativeSession.Attach(statePath);
          using var scrollExecutor =
            new ActionExecutor(statePath, scrollSession.Registry);
          int scrollCardId = RequiredInt(args, "--card-id");
          ScreenPoint? scrolled =
            scrollExecutor.ResolveClickPosition(
              scrollSession.Registry, scrollCardId);
          result = scrolled is null
            ? throw new InvalidOperationException(
              $"Card {scrollCardId} could not be scrolled on screen.")
            : new ActionExecution(
              0,
              "scroll-to",
              scrollCardId.ToString(),
              scrollCardId,
              $"Scrolled card {scrollCardId} to {scrolled.X},{scrolled.Y}.");
          break;
        }

        case "preview-card":
        {
          using NativeSession previewSession = NativeSession.Attach(statePath);
          using var previewExecutor =
            new ActionExecutor(statePath, previewSession.Registry);
          result = previewExecutor.PreviewCard(
            RequiredInt(args, "--card-id"));
          break;
        }

        case "preview-dialog":
        {
          using var previewExecutor = new ActionExecutor(statePath);
          result = previewExecutor.PreviewDialogItem(
            RequiredValue(args, "--name"),
            OptionalInt(args, "--index"));
          break;
        }

        case "select-dialog":
        {
          // Dialog items (trigger ordering, card selection lists) are
          // neither actions nor target selections, so Select refuses them.
          // This resolves the item geometrically and clicks a verified
          // visible point instead.
          using var dialogExecutor = new ActionExecutor(statePath);
          int? dialogIndex = OptionalInt(args, "--index");
          result = dialogExecutor.SelectDialogItem(
            RequiredValue(args, "--name"),
            dialogIndex);
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

        case "click-at":
        {
          // Raw coordinate click with no gates and no identity resolution.
          // This exists for probing elements the model cannot see (dialog
          // items without view models); it spends a real click, so it is a
          // manual test branch, not an agent path.
          int x = RequiredInt(args, "--x");
          int y = RequiredInt(args, "--y");
          int holdMs = OptionalInt(args, "--hold-ms") ?? 0;
          if (holdMs > 0)
            UiaSession.HoldAt(x, y, holdMs);
          else
            UiaSession.ClickAt(x, y);
          result = new ActionExecution(
            0,
            "click-at",
            $"{x},{y}",
            null,
            $"Clicked coordinates {x},{y}"
              + (holdMs > 0 ? $" held {holdMs}ms." : "."));
          break;
        }

        default:
          throw new ArgumentException(
            "Use 'uia dump-tree [--automation-id ID | --name-contains TEXT] "
              + "[--depth N] [--properties] [--ancestors] [--raw]', "
              + "'uia dump-tree --find-name TEXT [--raw] [--ancestors]', "
              + "'uia select (--card-id N | --player NAME)', "
            + "'uia select-dialog --name CARD', "
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
