using FlaUI.Core.Definitions;

using AlphaMTG.MTGOAgent.UIA.Models;

namespace AlphaMTG.MTGOAgent.UIA;

internal static class PlayerMapper
{
  public static Dictionary<string, PlayerView> Find(
    IReadOnlyList<Inspector.Observed> elements,
    IReadOnlyList<PlayerState> players
  )
  {
    var result = new Dictionary<string, PlayerView>();
    var playerBoxes = elements
      .Where(element =>
        element.Visible
        && element.AutomationId == "PlayerBox")
      .ToList();

    foreach (var player in players)
    {
      var playerBox = playerBoxes.FirstOrDefault(box =>
        elements.Any(element =>
          element.Root == box.Root
          && IsWithin(element.Path, box.Path)
          && string.Equals(
            element.Name,
            player.Name,
            StringComparison.OrdinalIgnoreCase)));

      if (playerBox is null)
        continue;

      var descendants = elements
        .Where(element =>
          element.Root == playerBox.Root
          && IsWithin(element.Path, playerBox.Path))
        .ToList();
      var avatar = descendants
        .Where(element =>
          element.Visible
          && element.ControlType == ControlType.Image
          && element.Bounds.Width >= 40
          && element.Bounds.Height >= 40)
        .OrderByDescending(element =>
          element.Bounds.Width * element.Bounds.Height)
        .FirstOrDefault()
        ?? playerBox;

      result[avatar.Key] = new PlayerView(player.UserId, player.Name);
    }

    return result;
  }

  private static bool IsWithin(
    IReadOnlyList<int> path,
    IReadOnlyList<int> scope
  ) =>
    path.Count >= scope.Count
    && path.Take(scope.Count).SequenceEqual(scope);
}
