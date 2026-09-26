/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;

namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// Read-only dump of MTGO's UIA tree, focused on what the accessibility layer
/// can actually observe about a card's selectable affordance.
/// </summary>
/// <remarks>
/// This command inspects only. It clicks nothing and mutates nothing, so it
/// is safe to run while MTGO is mid-interaction.
/// </remarks>
internal static class TreeDumpCommand
{
  public static int Run(string[] args)
  {
    try
    {
      string? automationIdFilter = OptionalValue(args, "--automation-id");
      string? nameFilter = OptionalValue(args, "--name-contains");
      int depth = OptionalInt(args, "--depth") ?? 6;
      bool includeProperties = args.Contains("--properties");
      bool includeAncestors = args.Contains("--ancestors");
      bool includePatterns = args.Contains("--patterns");
      bool rawView = args.Contains("--raw");

      // A targeted lookup: find elements whose name matches, in either view.
      string? findName = OptionalValue(args, "--find-name");
      if (findName is not null)
        return FindByName(findName, rawView, includeAncestors);

      using var automation = new UIA2Automation();
      var dump = new TreeDumpReport(DateTimeOffset.UtcNow);

      int[] processIds = System.Diagnostics.Process
        .GetProcessesByName("MTGO")
        .Select(process => process.Id)
        .ToArray();

      foreach (AutomationElement window in automation
        .GetDesktop()
        .FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
      {
        if (!processIds.Contains(window.FrameworkAutomationElement.ProcessId))
          continue;

        AutomationElement? scope = FindScope(window, automationIdFilter, nameFilter);
        if (scope is null) continue;

        dump.Matches.Add(
          Describe(scope, 0, depth, includeProperties, includePatterns, rawView));
        if (includeAncestors)
        {
          for (AutomationElement? parent = scope.Parent; parent is not null;
            parent = parent.Parent)
          {
            dump.Ancestors.Add(Describe(
              parent, 0, 1, includeProperties, includePatterns, rawView));
          }
        }
      }

      Console.WriteLine(JsonSerializer.Serialize(dump, AgentJson.Options));
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }

  private static AutomationElement? FindScope(
    AutomationElement window,
    string? automationId,
    string? nameContains)
  {
    if (automationId is null && nameContains is null) return window;

    foreach (AutomationElement element in window.FindAllDescendants())
    {
      bool automationIdMatches = automationId is null
        || string.Equals(
          UiaSession.GetAutomationId(element),
          automationId,
          StringComparison.Ordinal);
      bool nameMatches = nameContains is null
        || (UiaSession.GetName(element)?.Contains(
          nameContains, StringComparison.OrdinalIgnoreCase) ?? false);

      if (automationIdMatches && nameMatches) return element;
    }

    return null;
  }

  /// <summary>
  /// Finds every element carrying a given name and reports how many there
  /// were, so a name-based lookup can be proven unambiguous rather than
  /// assumed to be.
  /// </summary>
  private static int FindByName(
    string name,
    bool rawView,
    bool includeAncestors)
  {
    using var automation = new UIA2Automation();
    int[] processIds = System.Diagnostics.Process
      .GetProcessesByName("MTGO")
      .Select(process => process.Id)
      .ToArray();

    var report = new NameLookupReport(name, rawView);
    foreach (AutomationElement window in automation
      .GetDesktop()
      .FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
    {
      if (!processIds.Contains(window.FrameworkAutomationElement.ProcessId))
        continue;

      foreach (AutomationElement match in GetChildren(window, rawView)
        .Where(element => string.Equals(
          UiaSession.GetName(element), name, StringComparison.Ordinal)))
      {
        var hit = new NameHit(
          UiaSession.GetControlType(match).ToString(),
          UiaSession.GetAutomationId(match),
          UiaSession.GetClassName(match),
          FormatBounds(UiaSession.GetBounds(match)),
          AncestorChain(match, includeAncestors));

        report.Matches.Add(hit);
      }
    }

    Console.WriteLine(JsonSerializer.Serialize(report, AgentJson.Options));
    return report.Matches.Count == 1 ? 0 : 1;
  }

  private static List<string> AncestorChain(
    AutomationElement element,
    bool includeAncestors)
  {
    var chain = new List<string>();
    if (!includeAncestors) return chain;

    for (AutomationElement? parent = element.Parent; parent is not null;
      parent = parent.Parent)
    {
      chain.Add(string.Join("|", new[]
      {
        UiaSession.GetControlType(parent).ToString(),
        UiaSession.GetAutomationId(parent) ?? "-",
        UiaSession.GetClassName(parent) ?? "-"
      }));
    }

    return chain;
  }

  private sealed record NameLookupReport(string Name, bool RawView)
  {
    public List<NameHit> Matches { get; } = [];
  }

  private sealed record NameHit(
    string ControlType,
    string? AutomationId,
    string? ClassName,
    string? Bounds,
    List<string> Ancestors);

  private static TreeNode Describe(
    AutomationElement element,
    int level,
    int maxDepth,
    bool includeProperties,
    bool includePatterns = false,
    bool rawView = false)
  {
    var node = new TreeNode(
      level,
      UiaSession.GetControlType(element).ToString(),
      UiaSession.GetAutomationId(element),
      UiaSession.GetName(element),
      UiaSession.GetClassName(element),
      UiaSession.GetIsEnabled(element),
      UiaSession.GetIsOffscreen(element),
      UiaSession.IsVisible(element),
      FormatBounds(UiaSession.GetBounds(element)));

    if (includeProperties)
    {
      var properties = new SortedDictionary<string, string?>(StringComparer.Ordinal);
      foreach (string name in new[]
      {
        "AutomationId", "Name", "ClassName", "FrameworkId", "HelpText",
        "ItemStatus", "LocalizedControlType", "AriaRole", "ControllerFor",
        "LabeledBy", "IsEnabled", "IsKeyboardFocusable", "IsOffscreen",
        "IsContentElement", "IsControlElement", "AccessKey", "ProcessId"
      })
      {
        properties[name] = ReadProperty(element, name);
      }

      node = node with { Properties = properties };
    }

    if (includePatterns)
    {
      var patterns = new SortedDictionary<string, string?>(StringComparer.Ordinal);
      foreach ((string name, Func<bool> probe) in new (string, Func<bool>)[]
      {
        ("Invoke", () => element.Patterns.Invoke.Pattern is not null),
        ("LegacyIAccessible", () =>
          element.Patterns.LegacyIAccessible.Pattern is not null),
        ("SelectionItem", () =>
          element.Patterns.SelectionItem.Pattern is not null),
        ("Toggle", () => element.Patterns.Toggle.Pattern is not null),
        ("ExpandCollapse", () =>
          element.Patterns.ExpandCollapse.Pattern is not null),
        ("Value", () => element.Patterns.Value.Pattern is not null),
        ("Dock", () => element.Patterns.Dock.Pattern is not null),
        ("Text", () => element.Patterns.Text.Pattern is not null),
        ("ScrollItem", () => element.Patterns.ScrollItem.Pattern is not null)
      })
      {
        try { patterns[name] = probe() ? "supported" : "absent"; }
        catch (Exception exception)
        {
          patterns[name] = exception.InnerException?.GetType().Name
            ?? exception.GetType().Name;
        }
      }

      // LegacyIAccessible carries the action strings WPF surfaces for
      // elements that support a default (invoke) action.
      if (patterns["LegacyIAccessible"] == "supported")
      {
        try
        {
          patterns["  DefaultAction"] =
            element.Patterns.LegacyIAccessible.Pattern.DefaultAction;
          patterns["  Help"] = element.Patterns.LegacyIAccessible.Pattern.Help;
        }
        catch (Exception exception)
        {
          patterns["  DefaultAction"] = exception.GetType().Name;
        }
      }

      if (patterns["SelectionItem"] == "supported")
      {
        try
        {
          patterns["  IsSelected"] =
            element.Patterns.SelectionItem.Pattern.IsSelected.ToString();
        }
        catch (Exception exception)
        {
          patterns["  IsSelected"] = exception.GetType().Name;
        }
      }

      node = node with { Patterns = patterns };
    }

    if (level >= maxDepth) return node;

    // The control view hides WPF content that carries no interactive
    // semantics -- notably the TextBlock a player name is bound to. The raw
    // view is what exposes it, so the walk is view-selectable.
    foreach (AutomationElement child in GetChildren(element, rawView))
    {
      node.Children.Add(Describe(
        child, level + 1, maxDepth, includeProperties, includePatterns, rawView));
    }

    return node;
  }

  private static IEnumerable<AutomationElement> GetChildren(
    AutomationElement element,
    bool rawView)
  {
    if (!rawView)
      return element.FindAllChildren();

    // FindAllChildren() walks the control view, which drops WPF content that
    // carries no interactive semantics -- notably the TextBlock the player
    // name is bound to. A Descendants + TrueCondition search walks the raw
    // tree and does surface it.
    return element.FindAll(
      TreeScope.Descendants,
      FlaUI.Core.Conditions.TrueCondition.Default);
  }

  private static string? ReadProperty(AutomationElement element, string name)
  {
    try
    {
      object properties = element.Properties;
      System.Reflection.PropertyInfo? property =
        properties.GetType().GetProperty(name);
      object? value = property?.GetValue(properties);
      return value switch
      {
        null => null,
        System.Drawing.Rectangle rectangle => $"{rectangle.X},{rectangle.Y} "
          + $"{rectangle.Width}x{rectangle.Height}",
        System.Drawing.Color color => color.ToString(),
        _ => Convert.ToString(value)
      };
    }
    catch
    {
      return null;
    }
  }

  private static string? FormatBounds(System.Drawing.Rectangle? bounds) =>
    bounds is null
      ? null
      : $"{bounds.Value.X},{bounds.Value.Y} "
        + $"{bounds.Value.Width}x{bounds.Value.Height}";

  private static string? OptionalValue(string[] args, string name)
  {
    int index = Array.IndexOf(args, name);
    return index < 0 || index + 1 >= args.Length
      ? null
      : args[index + 1];
  }

  private static int? OptionalInt(string[] args, string name)
  {
    string? value = OptionalValue(args, name);
    return value is not null && int.TryParse(value, out int parsed)
      ? parsed
      : null;
  }

  private sealed record TreeDumpReport(DateTimeOffset CapturedAtUtc)
  {
    public List<TreeNode> Matches { get; init; } = [];

    public List<TreeNode> Ancestors { get; init; } = [];
  }

  private sealed record TreeNode(
    int Level,
    string ControlType,
    string? AutomationId,
    string? Name,
    string? ClassName,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsVisible,
    string? Bounds)
  {
    public SortedDictionary<string, string?>? Properties { get; init; }

    public SortedDictionary<string, string?>? Patterns { get; init; }

    public List<TreeNode> Children { get; } = [];
  }
}
