/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;
using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// Scrollable graveyard/exile zone lists with their viewports and scroll buttons.
/// </summary>
internal sealed class ZonePanels(UIASession session)
{
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
    foreach (AutomationElement window in session.GetMtgoWindows())
    {
      foreach (AutomationElement panel in UIASession.RawDescendants(window))
      {
        if (!string.Equals(
          UIASession.GetClassName(panel), "VerticalPanelView", StringComparison.Ordinal))
        {
          continue;
        }

        // The side view holds both the player box (which names the player)
        // and the zone list, so climb to it and confirm the owner before
        // accepting the panel.
        AutomationElement? sideView = ClimbToClass(panel, "PlayerInfoSideView");
        if (sideView is null) continue;
        if (!SideViewNamesPlayer(sideView, playerName)) continue;

        Rectangle? viewport = UIASession.GetBounds(panel);
        if (viewport is null) continue;
        AutomationElement? up = null;
        AutomationElement? down = null;
        foreach (AutomationElement child in UIASession.RawDescendants(sideView))
        {
          string? automationId = UIASession.GetAutomationId(child);
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
        UIASession.GetClassName(current), className, StringComparison.Ordinal))
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
    foreach (AutomationElement candidate in UIASession.RawDescendants(sideView))
    {
      if (!string.Equals(
        UIASession.GetName(candidate), playerName, StringComparison.Ordinal))
      {
        continue;
      }

      if (ClimbToClass(candidate, "PlayerInfoDetailsView") is not null)
        return true;
    }

    return false;
  }

}

/// <summary>
/// A scrollable zone list viewport with its scroll buttons.
/// </summary>
/// <param name="Viewport">The visible rectangle of the list.</param>
/// <param name="UpButton">Scrolls toward earlier entries.</param>
/// <param name="DownButton">Scrolls toward later entries.</param>
internal sealed record ZoneListPanel(
  Rectangle Viewport,
  AutomationElement UpButton,
  AutomationElement DownButton);
