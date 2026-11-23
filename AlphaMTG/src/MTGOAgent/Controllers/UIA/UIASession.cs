/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;


namespace AlphaMTG.MTGOAgent.Controllers.UIA;

/// <summary>
/// Locates player mats in MTGO's UI Automation tree.
/// </summary>
internal sealed class UIASession : IDisposable
{
  private readonly UIA2Automation _automation;

  public UIASession()
  {
    using var timing = AlphaMTGPlugin.Activities.StartActivity("alpha.uia.session");
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

  /// <summary>
  /// Finds the player mat for a player name.
  /// </summary>
  /// <remarks>
  /// Search only the player panels in the supplied duel window. Looking for a
  /// name throughout the window also visits every card and chat entry.
  /// </remarks>
  public AutomationElement? FindPlayerBox(string playerName, IntPtr windowHandle)
  {
    using var timing = AlphaMTGPlugin.Activities.StartActivity("alpha.uia.find_player");
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

}
