/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;
using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA2;

namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// Locates MTGO's WPF controls in the UI Automation tree.
/// </summary>
/// <remarks>
/// <para>
/// This layer only observes the client. It knows how to find a card element or
/// a prompt button so a human-equivalent click can be delivered, but it makes
/// no decision about game rules, target legality, or action sequencing — MTGO
/// remains the sole authority for those.
/// </para>
/// <para>
/// The <c>handView</c> and <c>DuelBattlefield</c> automation IDs are observed
/// UIA selectors for MTGO's own containers, not a model of the game.
/// </para>
/// </remarks>
internal sealed class UiaSession : IDisposable
{
  private const string HandScope = "handView";
  private const string BattlefieldScope = "DuelBattlefield";

  private readonly UIA2Automation _automation = new();

  public void Dispose() => _automation.Dispose();

  /// <summary>
  /// Enumerates the visible MTGO windows.
  /// </summary>
  public IReadOnlyList<AutomationElement> GetMtgoWindows()
  {
    int[] processIds = Process
      .GetProcessesByName("MTGO")
      .Select(process => process.Id)
      .ToArray();

    var windows = new List<AutomationElement>();
    foreach (AutomationElement window in _automation
      .GetDesktop()
      .FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
    {
      if (processIds.Contains(window.FrameworkAutomationElement.ProcessId))
        windows.Add(window);
    }

    return windows;
  }

  public static bool IsVisible(AutomationElement element)
  {
    if (element is null) return false;

    try
    {
      return !element.Properties.IsOffscreen.ValueOrDefault
        && element.Properties.BoundingRectangle.ValueOrDefault is { } bounds
        && bounds.Width > 0
        && bounds.Height > 0;
    }
    catch
    {
      return false;
    }
  }

  /// <summary>
  /// Clicks a point on screen.
  /// </summary>
  /// <remarks>
  /// Used when the target was resolved natively rather than through the
  /// automation tree, so there is no element to invoke. Every MTGO pattern a
  /// person would normally use (Invoke, LegacyIAccessible) is unsupported by
  /// this client's UI, so a coordinate click is the only way to deliver one.
  /// </summary>
  /// <remarks>
  /// The move, press, and release are sequenced with delays between them. A
  /// back-to-back down/up pair arrives as a hover (the cursor moves and the
  /// highlight follows) without ever registering as a click.
  /// </remarks>
  public static void ClickAt(int x, int y)
  {
    var point = new Point(x, y);
    Mouse.MoveTo(point);
    Thread.Sleep(250);
    Mouse.Down(MouseButton.Left);
    Thread.Sleep(150);
    Mouse.Up(MouseButton.Left);
  }

  /// <summary>
  /// Double-clicks a point on screen, sequenced like <see cref="ClickAt"/>.
  /// </summary>
  /// <remarks>
  /// Selection dialogs take a double click to choose an item; a single click
  /// only moves hover there.
  /// </remarks>
  public static void DoubleClickAt(int x, int y)
  {
    var point = new Point(x, y);
    Mouse.MoveTo(point);
    Thread.Sleep(250);
    Mouse.Down(MouseButton.Left);
    Thread.Sleep(120);
    Mouse.Up(MouseButton.Left);
    Thread.Sleep(150);
    Mouse.Down(MouseButton.Left);
    Thread.Sleep(120);
    Mouse.Up(MouseButton.Left);
  }

  /// <summary>
  /// Presses down at a point, holds, and releases without moving.
  /// </summary>
  /// <remarks>
  /// Some controls act on press-and-hold (pick-up semantics) rather than a
  /// quick click. Anything above a couple of seconds risks being read as a
  /// drag; keep holds short.
  /// </remarks>
  public static void HoldAt(int x, int y, int holdMs)
  {
    var point = new Point(x, y);
    Mouse.MoveTo(point);
    Thread.Sleep(250);
    Mouse.Down(MouseButton.Left);
    Thread.Sleep(Math.Max(50, holdMs));
    Mouse.Up(MouseButton.Left);
  }

  public static string? GetAutomationId(AutomationElement element) =>
    NullIfEmpty(element.Properties.AutomationId.ValueOrDefault);

  public static string? GetName(AutomationElement element) =>
    NullIfEmpty(element.Properties.Name.ValueOrDefault);

  public static bool GetIsEnabled(AutomationElement element)
  {
    try { return element.Properties.IsEnabled.ValueOrDefault; }
    catch { return false; }
  }

  public static bool GetIsOffscreen(AutomationElement element)
  {
    try { return element.Properties.IsOffscreen.ValueOrDefault; }
    catch { return true; }
  }

  public static Rectangle? GetBounds(AutomationElement element)
  {
    try
    {
      Rectangle bounds = element.Properties.BoundingRectangle.ValueOrDefault;
      return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }
    catch
    {
      return null;
    }
  }

  public static string? NullIfEmpty(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;

  private static AutomationElement? FindVisibleElement(
    AutomationElement root,
    string? automationId,
    string? name)
  {
    foreach (AutomationElement element in root.FindAllDescendants())
    {
      if (!IsVisible(element)) continue;
      if (automationId is not null
          && !string.Equals(
            GetAutomationId(element), automationId, StringComparison.Ordinal))
      {
        continue;
      }
      if (name is not null
          && !string.Equals(
            GetName(element), name, StringComparison.Ordinal))
      {
        continue;
      }
      return element;
    }

    return null;
  }

  /// <summary>
  /// Finds the visible cards in one zone, ordered the way MTGO lays them out.
  /// </summary>
  private IReadOnlyList<CardAnchor> GetZoneCards(string zone)
  {
    string scopeId = string.Equals(zone, "Hand", StringComparison.Ordinal)
      ? HandScope
      : BattlefieldScope;

    var anchors = new List<CardAnchor>();
    foreach (AutomationElement window in GetMtgoWindows())
    {
      AutomationElement? scope = FindVisibleElement(window, scopeId, null);
      if (scope is null) continue;

      var candidates = new List<CardCandidate>();
      foreach (AutomationElement element in scope.FindAllDescendants())
      {
        if (!IsVisible(element)) continue;

        string? automationId = GetAutomationId(element);
        string? name = GetCardName(element, automationId);
        Rectangle? bounds = GetBounds(element);
        if (name is null || automationId is null || bounds is null) continue;

        candidates.Add(new CardCandidate(name, automationId, bounds.Value));
      }

      if (candidates.Count == 0) continue;

      bool isHand = string.Equals(zone, "Hand", StringComparison.Ordinal);
      List<CardCandidate> ordered = isHand
        ? candidates.OrderBy(candidate => CenterX(candidate.Bounds)).ToList()
        : candidates
          .OrderBy(candidate => CenterY(candidate.Bounds))
          .ThenBy(candidate => CenterX(candidate.Bounds))
          .ToList();

      var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (CardCandidate candidate in ordered)
      {
        ordinals.TryGetValue(candidate.Name, out int ordinal);
        ordinals[candidate.Name] = ordinal + 1;
        anchors.Add(new CardAnchor(
          zone,
          candidate.Name,
          ordinal,
          candidate.AutomationId,
          candidate.Bounds));
      }
    }

    return anchors;
  }

  /// <summary>
  /// Resolves the clickable element for a card identified by name and its
  /// per-name occurrence, which is how MTGO disambiguates identical cards.
  /// </summary>
  public AutomationElement? FindCardElement(string? zone, string name, int ordinal)
  {
    string zoneName = string.IsNullOrEmpty(zone) ? "Battlefield" : zone;
    foreach (CardAnchor anchor in GetZoneCards(zoneName))
    {
      if (!string.Equals(anchor.Name, name, StringComparison.OrdinalIgnoreCase))
        continue;
      if (anchor.Ordinal != ordinal) continue;

      return FindByAutomationId(anchor.AutomationId, anchor.Bounds);
    }

    return null;
  }

  private AutomationElement? FindByAutomationId(string automationId, Rectangle bounds)
  {
    foreach (AutomationElement window in GetMtgoWindows())
    {
      AutomationElement? match = window
        .FindAllDescendants()
        .FirstOrDefault(element =>
          IsVisible(element)
          && string.Equals(
            GetAutomationId(element), automationId, StringComparison.Ordinal)
          && GetBounds(element) == bounds);
      if (match is not null) return match;
    }

    return null;
  }

  /// <summary>
  /// Finds a player's scrollable zone list (graveyard, exile) in the duel
  /// side panel, with its viewport and scroll buttons.
  /// </summary>
  /// <remarks>
  /// Graveyard and exile rows are realized views positioned past the visible
  /// viewport when scrolled out, so a native coordinate alone can point at
  /// pixels showing something else. The panel itself is a fixed viewport with
  /// up/down repeat buttons; scrolling it is a UI Automation operation, which
  /// is why this lives here rather than in the native registry.
  /// </remarks>
  public ZoneListPanel? FindZoneListPanel(string playerName)
  {
    foreach (AutomationElement window in GetMtgoWindows())
    {
      foreach (AutomationElement panel in RawDescendants(window))
      {
        if (!string.Equals(
          GetClassName(panel), "VerticalPanelView", StringComparison.Ordinal))
        {
          continue;
        }

        // The side view holds both the player box (which names the player)
        // and the zone list, so climb to it and confirm the owner before
        // accepting the panel.
        AutomationElement? sideView = ClimbToClass(panel, "PlayerInfoSideView");
        if (sideView is null) continue;
        if (!SideViewNamesPlayer(sideView, playerName)) continue;

        Rectangle? viewport = GetBounds(panel);
        if (viewport is null) continue;
        AutomationElement? up = null;
        AutomationElement? down = null;
        foreach (AutomationElement child in RawDescendants(sideView))
        {
          string? automationId = GetAutomationId(child);
          if (string.Equals(automationId, "UpArrow", StringComparison.Ordinal))
            up ??= child;
          else if (string.Equals(
            automationId, "DownArrow", StringComparison.Ordinal))
          {
            down ??= child;
          }
        }

        if (up is null || down is null) continue;
        return new ZoneListPanel(viewport.Value, up, down);
      }
    }

    return null;
  }

  private static AutomationElement? ClimbToClass(
    AutomationElement element,
    string className)
  {
    for (AutomationElement? current = element; current is not null;
      current = current.Parent)
    {
      if (string.Equals(
        GetClassName(current), className, StringComparison.Ordinal))
      {
        return current;
      }
    }

    return null;
  }

  private static bool SideViewNamesPlayer(
    AutomationElement sideView,
    string playerName)
  {
    foreach (AutomationElement candidate in RawDescendants(sideView))
    {
      if (!string.Equals(
        GetName(candidate), playerName, StringComparison.Ordinal))
      {
        continue;
      }

      if (ClimbToClass(candidate, "PlayerInfoDetailsView") is not null)
        return true;
    }

    return false;
  }

  /// <summary>
  /// A scrollable zone list viewport with its scroll buttons.
  /// </summary>
  /// <param name="Viewport">The visible rectangle of the list.</param>
  /// <param name="UpButton">Scrolls toward earlier entries.</param>
  /// <param name="DownButton">Scrolls toward later entries.</param>
  public sealed record ZoneListPanel(
    Rectangle Viewport,
    AutomationElement UpButton,
    AutomationElement DownButton);

  /// <summary>
  /// Finds the player mat for a player name.
  /// </summary>
  /// <remarks>
  /// The name is bound to a TextBlock that only appears in the raw view, and
  /// the same text occurs elsewhere in the client (lobby, nav, play-lobby).
  /// So the search is scoped to the duel scene, and the match is confirmed by
  /// climbing to the enclosing <c>PlayerInfoDetailsView</c> rather than
  /// trusting the name to be unique.
  /// </remarks>
  public AutomationElement? FindPlayerBox(string playerName)
  {
    // The player name is invisible in the control view; only the raw view
    // exposes the TextBlock it is bound to. Scope cannot come from finding a
    // "DuelScene" element first, because that element carries no automation id
    // in either view. Instead every raw-view name match is climbed, and only
    // a match sitting inside a player box inside the duel scene is accepted.
    foreach (AutomationElement window in GetMtgoWindows())
    {
      foreach (AutomationElement candidate in RawDescendants(window))
      {
        if (!string.Equals(
          UiaSession.GetName(candidate),
          playerName,
          StringComparison.Ordinal))
        {
          continue;
        }

        AutomationElement? box = ClimbToPlayerBoxInDuelScene(candidate);
        if (box is not null) return box;
      }
    }

    return null;
  }

  /// <summary>
  /// Climbs from a name match to the player box, and only accepts it when the
  /// box sits inside the duel scene. The name alone is not enough: it also
  /// appears in the match lobby side panel, the commander dialog, and the
  /// game log, none of which may be clicked as a target.
  /// </summary>
  private static AutomationElement? ClimbToPlayerBoxInDuelScene(
    AutomationElement element)
  {
    AutomationElement? box = null;
    for (AutomationElement? current = element; current is not null;
      current = current.Parent)
    {
      string? className = GetClassName(current);
      if (box is null)
      {
        if (string.Equals(
          className,
          "PlayerInfoDetailsView",
          StringComparison.Ordinal))
        {
          box = current;
        }
      }
      else if (string.Equals(
        className,
        "DuelScene",
        StringComparison.Ordinal))
      {
        return box;
      }
    }

    return null;
  }

  private static AutomationElement[] RawDescendants(AutomationElement root) =>
    root.FindAll(
      TreeScope.Descendants,
      FlaUI.Core.Conditions.TrueCondition.Default);

  /// <summary>
  /// Names of the UI Automation patterns an element supports.
  /// Empty when pattern enumeration itself is unsupported.
  /// </summary>
  public static IReadOnlyList<string> GetPatterns(AutomationElement element)
  {
    var patterns = new List<string>();
    try
    {
      foreach (var pattern in element.GetSupportedPatterns())
      {
        try
        {
          patterns.Add(pattern.Name);
        }
        catch
        {
          // Unnamed pattern; skip it.
        }
      }
    }
    catch
    {
      // Pattern enumeration unsupported; leave empty.
    }

    return patterns;
  }

  /// <summary>
  /// A raw image element with its screen bounds.
  /// </summary>
  /// <param name="AutomationId">The element's automation id, if any.</param>
  /// <param name="Bounds">Screen bounds. Elements without bounds are skipped.</param>
  /// <param name="Patterns">Supported pattern names, when requested.</param>
  /// <param name="ParentClassName">Immediate UIA container class.</param>
  public sealed record RawImage(
    string? AutomationId,
    Rectangle Bounds,
    IReadOnlyList<string> Patterns,
    string? ParentClassName);

  /// <summary>
  /// Lists every raw image element across the client's windows.
  /// </summary>
  /// <remarks>
  /// Card art in dialogs and zone lists often carries no name, so candidate
  /// click targets are found by geometry instead of identity.
  /// </remarks>
  public IReadOnlyList<RawImage> FindImages()
  {
    var images = new List<RawImage>();
    foreach (AutomationElement window in GetMtgoWindows())
    {
      foreach (AutomationElement element in RawDescendants(window))
      {
        if (!string.Equals(
          GetControlType(element).ToString(),
          "Image",
          StringComparison.Ordinal))
        {
          continue;
        }

        Rectangle? bounds = GetBounds(element);
        if (bounds is null) continue;

        images.Add(new RawImage(
          GetAutomationId(element), bounds.Value, GetPatterns(element),
          element.Parent is AutomationElement parent
            ? GetClassName(parent) : null));
      }
    }

    return images;
  }

  /// <summary>
  /// Finds an enabled, visible control whose label matches, preferring the
  /// prompt popups MTGO raises over the battlefield itself.
  /// </summary>
  public AutomationElement? FindControlByLabel(string label)
  {
    var fallback = new List<AutomationElement>();
    foreach (AutomationElement window in GetMtgoWindows())
    {
      foreach (AutomationElement element in window.FindAllDescendants())
      {
        if (!IsVisible(element) || !GetIsEnabled(element)) continue;
        if (!LabelsMatch(GetName(element), label)) continue;

        if (GetAutomationId(element)?.StartsWith("popup:", StringComparison.Ordinal)
            == true
            || IsInsidePopup(element))
        {
          return element;
        }

        fallback.Add(element);
      }
    }

    return fallback.FirstOrDefault();
  }

  private static bool IsInsidePopup(AutomationElement element)
  {
    try
    {
      for (AutomationElement? parent = element.Parent; parent is not null;
        parent = parent.Parent)
      {
        if (GetAutomationId(parent)?.StartsWith("popup:", StringComparison.Ordinal)
            == true)
        {
          return true;
        }
      }
    }
    catch
    {
      // An ancestor chain can be severed while MTGO rebuilds its tree.
    }

    return false;
  }

  /// <summary>
  /// Waits for the card context menu MTGO opens on a right click.
  /// </summary>
  public AutomationElement? WaitForContextMenu(TimeSpan timeout)
  {
    DateTime deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
      AutomationElement? menu = FindVisibleContextMenu();
      if (menu is not null) return menu;
      Thread.Sleep(50);
    }

    return null;
  }

  private AutomationElement? FindVisibleContextMenu()
  {
    foreach (AutomationElement window in GetMtgoWindows())
    {
      AutomationElement? menu =
        window.FindAllDescendants().FirstOrDefault(element =>
          IsVisible(element)
          && string.Equals(
            GetClassName(element), "ContextMenu", StringComparison.Ordinal));
      if (menu is not null) return menu;
    }

    return null;
  }

  public bool HasContextMenu() => FindVisibleContextMenu() is not null;

  public void DismissContextMenu()
  {
    Keyboard.Type([VirtualKeyShort.ESCAPE]);
    Thread.Sleep(150);
  }

  public static ControlType GetControlType(AutomationElement element)
  {
    try { return element.Properties.ControlType.ValueOrDefault; }
    catch { return ControlType.Custom; }
  }

  public static string? GetClassName(AutomationElement element)
  {
    try
    {
      return NullIfEmpty(element.Properties.ClassName.ValueOrDefault);
    }
    catch
    {
      return null;
    }
  }

  private static int CenterX(Rectangle bounds) => bounds.X + bounds.Width / 2;

  private static int CenterY(Rectangle bounds) => bounds.Y + bounds.Height / 2;

  private static string? GetCardName(AutomationElement element, string? automationId)
  {
    if (automationId?.StartsWith("Image-", StringComparison.Ordinal) == true)
      return NullIfEmpty(automationId["Image-".Length..]);

    // Basic lands expose only their type line, which carries the card name
    // after a "Basic <type> - <name>" separator.
    if (!string.Equals(automationId, "CardType", StringComparison.Ordinal))
      return null;

    string? typeLine = GetName(element);
    if (typeLine is null
        || !typeLine.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    int separator = Math.Max(
      typeLine.LastIndexOf(" - ", StringComparison.Ordinal),
      typeLine.LastIndexOf(" — ", StringComparison.Ordinal));
    return separator < 0
      ? null
      : NullIfEmpty(typeLine[(separator + 3)..]);
  }

  /// <summary>
  /// Compares a control label to an action name the way MTGO presents it,
  /// ignoring case, padding, and inner spacing.
  /// </summary>
  public static bool LabelsMatch(string? actual, string expected)
  {
    if (actual is null) return false;

    string Normalize(string value)
    {
      Span<char> buffer = stackalloc char[value.Length];
      int length = 0;
      bool previousSpace = false;
      foreach (char character in value.Trim())
      {
        if (char.IsWhiteSpace(character))
        {
          if (!previousSpace && length > 0) buffer[length++] = ' ';
          previousSpace = true;
          continue;
        }

        previousSpace = false;
        buffer[length++] = char.ToUpperInvariant(character);
      }

      return new string(buffer[..length]);
    }

    return string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);
  }

  private sealed record CardCandidate(
    string Name,
    string AutomationId,
    Rectangle Bounds);

  private sealed record CardAnchor(
    string Zone,
    string Name,
    int Ordinal,
    string AutomationId,
    Rectangle Bounds);
}
