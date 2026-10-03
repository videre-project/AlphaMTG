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
/// Player mats in the duel scene, resolved by name.
/// </summary>
internal sealed class PlayerBoxes(UIASession session)
{
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
    foreach (AutomationElement window in session.GetMtgoWindows())
    {
      foreach (AutomationElement candidate in UIASession.RawDescendants(window))
      {
        if (!string.Equals(
          UIASession.GetName(candidate),
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
      string? className = UIASession.GetClassName(current);
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
}
