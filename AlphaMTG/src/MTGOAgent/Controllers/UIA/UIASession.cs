/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;

using MTGOSDK.Core.Diagnostics;


namespace AlphaMTG.MTGOAgent.Controllers.UIA;

/// <summary>
/// Locates player mats and scroll controls in MTGO's UI Automation tree.
/// </summary>
internal sealed class UIASession : IDisposable
{
  private readonly UIA2Automation _automation;

  public UIASession()
  {
    using var timing = LocalTelemetry.Begin("alpha.uia.session");
    _automation = new();
  }

  public void Dispose() => _automation.Dispose();

  public static bool GetIsEnabled(AutomationElement element)
  {
    try { return element.Properties.IsEnabled.ValueOrDefault; }
    catch { return false; }
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

  private static string? GetClassName(AutomationElement element)
  {
    try { return element.Properties.ClassName.ValueOrDefault; }
    catch { return null; }
  }

  /// <summary>
  /// Finds the player mat for a player name.
  /// </summary>
  /// <remarks>
  /// Search only the player panels in the supplied duel window. Looking for a
  /// name throughout the window also visits every card and chat entry.
  /// </remarks>
  public AutomationElement? FindPlayerBox(string playerName, IntPtr windowHandle)
  {
    using var timing = LocalTelemetry.Begin("alpha.uia.find_player");
    foreach (AutomationElement box in PlayerBoxes(windowHandle))
    {
      if (box.FindFirstDescendant(cf => cf.ByName(playerName)) is not null)
        return box;
    }

    return null;
  }

  private IEnumerable<AutomationElement> PlayerBoxes(IntPtr windowHandle)
  {
    AutomationElement window = _automation.FromHandle(windowHandle);
    AutomationElement? scene = window.FindFirstDescendant(cf => cf.ByClassName("DuelScene"));
    AutomationElement? view = scene?.FindFirstChild(cf => cf.ByClassName("DuelView"));
    if (view is null) yield break;

    foreach (AutomationElement side in view.FindAllChildren(cf => cf.ByClassName("PlayerInfoSideView")))
    {
      AutomationElement? box = side.FindFirstChild(cf => cf.ByClassName("PlayerInfoDetailsView"));
      if (box is not null) yield return box;
    }
  }

  /// <summary>
  /// Finds a player's scrollable zone list (graveyard, exile) in the duel
  /// side panel, with its viewport and scroll buttons.
  /// </summary>
  /// <remarks>
  /// Graveyard and exile rows are realized views positioned past the visible
  /// viewport when scrolled out, so a native coordinate alone can point at
  /// pixels showing something else. The panel itself is a fixed viewport with
  /// up/down repeat buttons. UI Automation locates these controls and invokes
  /// them to bring the row into view.
  /// </remarks>
  public (Rectangle Viewport, AutomationElement UpButton, AutomationElement DownButton)?
    FindZoneListPanel(string playerName, IntPtr windowHandle)
  {
    AutomationElement? box = FindPlayerBox(playerName, windowHandle);
    AutomationElement? side = box is null ? null : ClimbToClass(box, "PlayerInfoSideView");
    AutomationElement? panel = side?.FindFirstChild(cf => cf.ByClassName("VerticalPanelView"));
    if (panel is null || GetBounds(panel) is not Rectangle viewport) return null;

    AutomationElement? up = side!.FindFirstDescendant(cf => cf.ByAutomationId("UpArrow"));
    AutomationElement? down = side.FindFirstDescendant(cf => cf.ByAutomationId("DownArrow"));
    return up is not null && down is not null ? (viewport, up, down) : null;
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
}
