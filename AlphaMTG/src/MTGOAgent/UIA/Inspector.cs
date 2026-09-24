using System.Diagnostics;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;

using AlphaMTG.MTGOAgent.UIA.Models;

namespace AlphaMTG.MTGOAgent.UIA;

internal sealed class Inspector : IDisposable
{
  private static readonly TimeSpan UiChangeTimeout = TimeSpan.FromSeconds(2);

  private readonly UIA2Automation _automation = new();
  private readonly HashSet<int> _processIds;

  public Inspector()
  {
    _processIds = [];
    foreach (var process in Process.GetProcesses())
    {
      try
      {
        if (process.ProcessName.Contains("MTGO", StringComparison.OrdinalIgnoreCase))
          _processIds.Add(process.Id);
      }
      finally
      {
        process.Dispose();
      }
    }

    if (_processIds.Count == 0)
      throw new InvalidOperationException("MTGO is not running.");
  }

  public Snapshot Capture(AgentState state)
  {
    var observed = Observe();
    var cards = CardMapper.Find(observed, state.Cards);
    var players = PlayerMapper.Find(observed, state.Players);
    var nodes = observed
      .Select(item => Describe(
        item,
        cards.GetValueOrDefault(item.Key),
        players.GetValueOrDefault(item.Key)
      ))
      .Where(node => node is not null)
      .Cast<Node>()
      .DistinctBy(node => node.Card is null
        ? (object)(node.Root, node.Path, node.Name, node.ControlType)
        : node.Reference)
      .ToList();

    AnnotateCards(nodes, state.Cards);
    return new Snapshot(Guid.NewGuid().ToString("N"), nodes);
  }

  public int PopupCount
  {
    get
    {
      var count = 0;
      foreach (var window in _automation.GetDesktop().FindAllChildren())
      {
        if (!_processIds.Contains(window.FrameworkAutomationElement.ProcessId))
          continue;

        if (window.ClassName == "ContextMenu")
          count++;
        count += window.FindAllDescendants(
          window.ConditionFactory.ByClassName("ContextMenu")
        ).Count();
      }

      return count;
    }
  }

  public Snapshot Perform(
    Snapshot snapshot,
    Node target,
    string operation,
    AgentState state,
    Func<bool>? waitFor = null
  )
  {
    if (!snapshot.Nodes.Contains(target))
      throw new InvalidOperationException("The UI element is not in this snapshot.");
    if (!target.Operations.Contains(operation, StringComparer.Ordinal))
      throw new InvalidOperationException(
        $"'{target.Reference}' does not support '{operation}'."
      );

    var root = FindRoots().SingleOrDefault(root => root.Id == target.Root)
      ?? throw new InvalidOperationException("The UI root no longer exists.");
    var element = Resolve(root.Element, target.Path);
    if (!SameIdentity(element, target))
      throw new InvalidOperationException("The UI element is stale.");

    // Subscribe before dispatching the input. The event is only a wake-up;
    // waitFor performs the actual, targeted condition check.
    using var poller = new UIAEventPoller(_automation, root.Element);
    Act(element, target, operation);
    if (waitFor is not null)
      poller.WaitFor(waitFor, UiChangeTimeout);

    return Capture(state);
  }

  private List<Observed> Observe()
  {
    var roots = FindRoots();
    if (roots.Count == 0)
      throw new InvalidOperationException("MTGO exposes no duel UIA root.");

    var observed = new List<Observed>();
    foreach (var root in roots)
      Walk(root, root.Element, [], observed);
    return observed;
  }

  private void Walk(
    LiveRoot root,
    AutomationElement element,
    IReadOnlyList<int> path,
    List<Observed> observed
  )
  {
    observed.Add(Read(root.Id, element, path));
    var children = element.FindAllChildren();
    for (var index = 0; index < children.Length; index++)
      Walk(root, children[index], [.. path, index], observed);
  }

  private List<LiveRoot> FindRoots()
  {
    var candidates = new List<(string Kind, AutomationElement Element)>();

    foreach (var window in _automation.GetDesktop().FindAllChildren())
    {
      if (!_processIds.Contains(window.FrameworkAutomationElement.ProcessId))
        continue;

      var descendants = window.FindAllDescendants();
      IEnumerable<AutomationElement> elements = [window, .. descendants];
      AutomationElement? gameDetails = elements.FirstOrDefault(element =>
        element.AutomationId == "GameDetailsWindow");

      if (gameDetails is not null)
      {
        candidates.Add(("game", gameDetails));
      }
      else
      {
        var duelElements = elements
          .Where(element => element.AutomationId == "DuelSceneWindow")
          .ToList();
        candidates.AddRange(
          duelElements
            .Where(Visible)
            .Select(element => ("duel", element)));

        if (duelElements.Count == 0 && descendants.Any(element =>
          element.AutomationId?.StartsWith(
            "TemporaryZone_",
            StringComparison.OrdinalIgnoreCase
          ) == true))
        {
          candidates.Add(("zone", window));
        }
      }

      // Context menus can be positioned below the game window. Keep their
      // UIA controls available for Invoke even when they are offscreen.
      candidates.AddRange(
        elements
          .Where(element => element.ClassName == "ContextMenu")
          .Select(element => ("popup", element)));
    }

    return candidates
      .DistinctBy(candidate => (candidate.Kind, candidate.Element.BoundingRectangle))
      .OrderBy(candidate => candidate.Kind)
      .ThenBy(candidate => candidate.Element.BoundingRectangle.X)
      .ThenBy(candidate => candidate.Element.BoundingRectangle.Y)
      .GroupBy(candidate => candidate.Kind)
      .SelectMany(group => group.Select((candidate, index) => new LiveRoot(
        $"{candidate.Kind}:{index}",
        candidate.Element
      )))
      .ToList();
  }

  private static Observed Read(
    string root,
    AutomationElement element,
    IReadOnlyList<int> path
  )
  {
    var rectangle = element.BoundingRectangle;
    return new Observed(
      root,
      path,
      element,
      EmptyToNull(element.AutomationId),
      EmptyToNull(element.Name),
      EmptyToNull(element.ClassName),
      element.ControlType,
      element.IsEnabled,
      !IsOffscreen(element) && rectangle.Width > 0 && rectangle.Height > 0,
      new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height)
    );
  }

  private static Node? Describe(
    Observed item,
    CardMark? card,
    PlayerView? player
  )
  {
    bool isPopup = item.Root.StartsWith("popup:", StringComparison.Ordinal);
    if (!item.Visible && !isPopup)
      return null;

    var operations = PatternOperations(item.Element);
    var pointer = item.Visible
      && item.Path.Count > 0
      && item.IsEnabled
      && (card is not null || player is not null || operations.Count > 0);
    if (pointer)
    {
      operations.Add("click");
      operations.Add("rightClick");
    }

    var popupLabel = isPopup && item.Name is not null;
    if (item.Path.Count > 0
        && card is null
        && player is null
        && operations.Count == 0
        && !popupLabel)
    {
      return null;
    }

    var clickPoint = pointer
      ? card?.ClickPoint ?? new Point(
        item.Bounds.X + item.Bounds.Width / 2,
        item.Bounds.Y + item.Bounds.Height / 2
      )
      : null;

    return new Node(
      card?.Reference ?? Reference(item.Root, item.Path),
      item.Root,
      item.Path,
      item.AutomationId,
      item.Name,
      item.ClassName,
      item.ControlType.ToString(),
      item.IsEnabled,
      item.Bounds,
      clickPoint,
      operations,
      card is null
        ? null
        : new CardView(card.Zone, card.ZoneInstance, card.Index, card.Name),
      null,
      player
    );
  }

  private static List<string> PatternOperations(AutomationElement element)
  {
    var operations = new List<string>();
    Add(operations, "invoke", () => element.Patterns.Invoke.IsSupported);
    return operations;
  }

  private static void Add(
    List<string> operations,
    string operation,
    Func<bool> supported
  )
  {
    if (supported())
      operations.Add(operation);
  }

  private static void Act(
    AutomationElement element,
    Node target,
    string operation
  )
  {
    switch (operation)
    {
      case "click":
      case "rightClick":
        var point = ClickPoint(target);
        Mouse.MoveTo(point);
        if (Mouse.Position != point)
          throw new InvalidOperationException($"Cursor did not reach {point}.");
        if (operation == "click")
          Mouse.LeftClick();
        else
          Mouse.RightClick();
        break;
      case "invoke":
        element.Patterns.Invoke.Pattern.Invoke();
        break;
      default:
        throw new ArgumentException($"Unsupported operation '{operation}'.");
    }
  }

  private static System.Drawing.Point ClickPoint(Node target) =>
    target.ClickPoint is { } point
      ? new System.Drawing.Point(point.X, point.Y)
      : throw new InvalidOperationException(
        $"'{target.Reference}' has no click point."
      );

  private static void AnnotateCards(
    List<Node> nodes,
    IReadOnlyList<CardState> cards
  )
  {
    var modelZones = cards
      .Where(card => card.Zone is not null)
      .GroupBy(card => (
        Zone: CardMapper.NormalizeZone(card.Zone!),
        card.OwnerIndex
      ))
      .Select(group => new
      {
        group.Key,
        Cards = group
          .OrderBy(card => card.ZoneIndex ?? int.MaxValue)
          .ThenBy(card => card.Id)
          .ToList()
      })
      .ToList();
    var used = new HashSet<(string Zone, int OwnerIndex)>();

    foreach (var uiZone in nodes
      .Where(node => node.Card is not null)
      .GroupBy(node => (node.Card!.Zone, node.Card.ZoneInstance)))
    {
      var uiCards = uiZone.OrderBy(node => node.Card!.Index).ToList();
      var orderedUiCards = uiCards
        .OrderBy(node => node.Card!.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
      var matches = modelZones.Where(model =>
        model.Key.Zone == uiZone.Key.Zone
        && !used.Contains(model.Key)
        && model.Cards.Count == uiCards.Count
        && model.Cards
          .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
          .Select(card => card.Name)
          .SequenceEqual(
            orderedUiCards.Select(node => node.Card!.Name),
            StringComparer.OrdinalIgnoreCase
          )
      ).ToList();

      if (matches.Count != 1)
        continue;

      var match = matches[0];
      used.Add(match.Key);
      var orderedModelCards = match.Cards
        .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
      for (var index = 0; index < orderedUiCards.Count; index++)
      {
        var nodeIndex = nodes.IndexOf(orderedUiCards[index]);
        nodes[nodeIndex] = orderedUiCards[index] with
        {
          CardId = orderedModelCards[index].Id
        };
      }
    }
  }

  private static AutomationElement Resolve(
    AutomationElement root,
    IReadOnlyList<int> path
  )
  {
    var current = root;
    foreach (var index in path)
    {
      var children = current.FindAllChildren();
      if (index >= children.Length)
        throw new InvalidOperationException("The UI tree changed.");
      current = children[index];
    }
    return current;
  }

  private static bool SameIdentity(AutomationElement element, Node node) =>
    EmptyToNull(element.AutomationId) == node.AutomationId
    && EmptyToNull(element.Name) == node.Name
    && EmptyToNull(element.ClassName) == node.ClassName
    && element.ControlType.ToString() == node.ControlType;

  private static string Reference(string root, IReadOnlyList<int> path) =>
    path.Count == 0 ? root : $"{root}/{string.Join('.', path)}";

  private static bool IsOffscreen(AutomationElement element)
  {
    try
    {
      return element.IsOffscreen;
    }
    catch
    {
      return false;
    }
  }

  private static bool Visible(AutomationElement element)
  {
    var rectangle = element.BoundingRectangle;
    return !IsOffscreen(element)
      && rectangle.Width > 0
      && rectangle.Height > 0;
  }

  private static string? EmptyToNull(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;

  public void Dispose() => _automation.Dispose();

  private sealed record LiveRoot(
    string Id,
    AutomationElement Element
  );

  internal sealed record Observed(
    string Root,
    IReadOnlyList<int> Path,
    AutomationElement Element,
    string? AutomationId,
    string? Name,
    string? ClassName,
    ControlType ControlType,
    bool IsEnabled,
    bool Visible,
    Bounds Bounds
  )
  {
    public string Key => $"{Root}/{string.Join('.', Path)}";
  }
}
