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
/// Reads MTGO's UI Automation tree for chrome the native model cannot see.
/// </summary>
/// <remarks>
/// <para>
/// Player boxes, zone-list panels, prompt buttons, and menus have no native
/// positions or view-models we can resolve, so this layer locates them by
/// walking the automation tree and reports screen bounds. Cards are never
/// located here: they resolve natively by id with positions from the duel
/// scene's own view-models.
/// </para>
/// <para>
/// This layer never clicks and never decides. Bounds flow into synthesized
/// selection; menus are driven by pattern invoke; game rules, legality, and
/// sequencing stay MTGO's sole authority.
/// </para>
/// </remarks>
internal sealed class UIASession : IDisposable
{
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

  public static string? GetAutomationId(AutomationElement element) =>
    NullIfEmpty(element.Properties.AutomationId.ValueOrDefault);

  public static string? GetName(AutomationElement element) =>
    NullIfEmpty(element.Properties.Name.ValueOrDefault);

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

  public static string? NullIfEmpty(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;





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

  public static ControlType GetControlType(AutomationElement element)
  {
    try { return element.Properties.ControlType.ValueOrDefault; }
    catch { return ControlType.Custom; }
  }

  public static string? GetClassName(AutomationElement element)
  {
    try { return element.Properties.ClassName.ValueOrDefault; }
    catch { return null; }
  }

  internal static AutomationElement[] RawDescendants(AutomationElement root) =>
    root.FindAll(
      TreeScope.Descendants,
      FlaUI.Core.Conditions.TrueCondition.Default);
}
