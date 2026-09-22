using AlphaMTG.MTGOAgent.UIA.Models;

namespace AlphaMTG.MTGOAgent.UIA;

internal static class CardMapper
{
  public static Dictionary<string, CardMark> Find(
    IReadOnlyList<Inspector.Observed> elements
  )
  {
    var cards = elements
      .Where(element => element.Visible)
      .Select(element => new Candidate(element, CardName(element)))
      .Where(candidate => candidate.Name is not null)
      .GroupBy(candidate => (candidate.Name, candidate.Element.Bounds))
      .Select(group => group.First())
      .Select(candidate => new
      {
        Card = candidate,
        Zone = FindZone(candidate.Element, elements)
      })
      .Where(item => item.Zone is not null)
      .ToList();

    var zones = cards
      .GroupBy(item => item.Zone!.Key)
      .Select(group => new ZoneCards(
        group.First().Zone!.Name,
        group.Key,
        group.Select(item => item.Card).ToList()
      ))
      .OrderBy(zone => zone.Key)
      .ToList();

    var result = new Dictionary<string, CardMark>();
    foreach (var sameName in zones.GroupBy(zone => zone.Name))
    {
      var instances = sameName.ToList();
      for (var instance = 0; instance < instances.Count; instance++)
        AddCards(instances[instance], instance, instances.Count, result);
    }
    return result;
  }

  private static ZoneScope? FindZone(
    Inspector.Observed card,
    IReadOnlyList<Inspector.Observed> elements
  )
  {
    var ancestors = elements
      .Where(element =>
        element.Root == card.Root
        && IsAncestor(element.Path, card.Path))
      .OrderByDescending(element => element.Path.Count);

    foreach (var ancestor in ancestors)
    {
      var markers = elements
        .Where(element =>
          element.Root == ancestor.Root
          && IsWithin(element.Path, ancestor.Path)
          && element.AutomationId?.StartsWith(
            "TemporaryZone_",
            StringComparison.OrdinalIgnoreCase) == true)
        .Select(element => NormalizeZone(
          element.AutomationId!["TemporaryZone_".Length..]))
        .Distinct()
        .ToList();

      if (markers.Count == 1)
        return new ZoneScope(markers[0], ScopeKey(ancestor));

      var identity = ancestor.AutomationId ?? ancestor.ClassName;
      if (identity?.Contains("Hand", StringComparison.OrdinalIgnoreCase) == true)
        return new ZoneScope("hand", ScopeKey(ancestor));
      if (identity?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true)
        return new ZoneScope("battlefield", ScopeKey(ancestor));
      if (identity?.Contains("Zone", StringComparison.OrdinalIgnoreCase) == true
        && !identity.Contains("Button", StringComparison.OrdinalIgnoreCase)
        && !identity.Contains("Splitter", StringComparison.OrdinalIgnoreCase))
      {
        return new ZoneScope(NormalizeZone(identity), ScopeKey(ancestor));
      }
    }

    return null;
  }

  private static void AddCards(
    ZoneCards zone,
    int instance,
    int instanceCount,
    Dictionary<string, CardMark> result
  )
  {
    var cards = zone.Cards
      .OrderBy(card => zone.Name == "hand"
        ? CenterX(card.Element.Bounds)
        : CenterY(card.Element.Bounds))
      .ThenBy(card => CenterX(card.Element.Bounds))
      .ToList();

    var imageY = cards
      .Where(card => card.Element.AutomationId?.StartsWith("Image-") == true)
      .Select(card => CenterY(card.Element.Bounds))
      .DefaultIfEmpty()
      .Average();
    var zoneId = instanceCount == 1 ? zone.Name : $"{zone.Name}:{instance}";

    for (var index = 0; index < cards.Count; index++)
    {
      var card = cards[index];
      var clickY = card.Element.AutomationId == "CardType"
        ? imageY > 0
          ? (int)Math.Round(imageY)
          : card.Element.Bounds.Y - 35
        : CenterY(card.Element.Bounds);

      result[card.Element.Key] = new CardMark(
        $"{zoneId}:{index}",
        zone.Name,
        instance,
        index,
        card.Name!,
        new Point(CenterX(card.Element.Bounds), clickY)
      );
    }
  }

  private static string? CardName(Inspector.Observed element)
  {
    if (element.AutomationId?.StartsWith("Image-", StringComparison.Ordinal) == true)
      return element.AutomationId["Image-".Length..];

    if (element.AutomationId != "CardType"
      || element.Name?.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) != true)
      return null;

    var separator = Math.Max(
      element.Name.LastIndexOf(" - ", StringComparison.Ordinal),
      element.Name.LastIndexOf(" — ", StringComparison.Ordinal)
    );
    return separator < 0 ? null : element.Name[(separator + 3)..];
  }

  internal static string NormalizeZone(string value)
  {
    foreach (var suffix in new[] { "_View", "View", "Component", "Window" })
      value = value.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);

    return string.Concat(value.Select((character, index) =>
      char.IsUpper(character) && index > 0
        ? $"-{char.ToLowerInvariant(character)}"
        : char.ToLowerInvariant(character).ToString()))
      .Replace("_", "-", StringComparison.Ordinal)
      .Trim('-');
  }

  private static string ScopeKey(Inspector.Observed element) =>
    $"{element.Root}/{string.Join('.', element.Path)}";

  private static bool IsAncestor(
    IReadOnlyList<int> ancestor,
    IReadOnlyList<int> path
  ) =>
    ancestor.Count < path.Count
    && path.Take(ancestor.Count).SequenceEqual(ancestor);

  private static bool IsWithin(
    IReadOnlyList<int> path,
    IReadOnlyList<int> scope
  ) =>
    path.Count >= scope.Count
    && path.Take(scope.Count).SequenceEqual(scope);

  private static int CenterX(Bounds bounds) => bounds.X + bounds.Width / 2;
  private static int CenterY(Bounds bounds) => bounds.Y + bounds.Height / 2;

  private sealed record Candidate(Inspector.Observed Element, string? Name);
  private sealed record ZoneScope(string Name, string Key);
  private sealed record ZoneCards(
    string Name,
    string Key,
    IReadOnlyList<Candidate> Cards
  );
}

internal sealed record CardMark(
  string Reference,
  string Zone,
  int ZoneInstance,
  int Index,
  string Name,
  Point ClickPoint
);
