using System.Text.Json;

using AlphaMTG.MTGOAgent.UIA.Models;


namespace AlphaMTG.MTGOAgent.UIA;

internal static class UIACommand
{
  public static int Run(string[] args)
  {
    try
    {
      var command = args.FirstOrDefault() ?? "snapshot";
      var statePath = AgentStateStore.DefaultPath;
      var state = AgentStateStore.Read(statePath);

      object result;
      if (command == "snapshot")
      {
        using var inspector = new Inspector();
        result = inspector.Capture(state);
      }
      else if (command == "execute-action")
      {
        using var executor = new ActionExecutor(statePath);
        var targetSet = OptionalInt(args, "--target-set");
        var targetId = OptionalInt(args, "--target-id");
        if (targetSet.HasValue != targetId.HasValue)
        {
          throw new ArgumentException(
            "--target-set and --target-id must be supplied together."
          );
        }
        result = executor.Execute(
          RequiredInt(args, "--game-id"),
          RequiredInt(args, "--nonce"),
          RequiredInt(args, "--action-id"),
          targetSet.HasValue
            ? new TargetSelection(targetSet.Value, targetId!.Value)
            : null
        );
      }
      else
      {
        throw new ArgumentException(
          "Use 'uia snapshot' or 'uia execute-action'."
        );
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

  private static int RequiredInt(string[] args, string name)
  {
    var index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length
        || !int.TryParse(args[index + 1], out var value))
    {
      throw new ArgumentException($"{name} requires an integer value.");
    }
    return value;
  }

  private static int? OptionalInt(string[] args, string name)
  {
    var index = Array.IndexOf(args, name);
    if (index < 0)
      return null;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value))
      throw new ArgumentException($"{name} requires an integer value.");
    return value;
  }
}
