using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;

namespace AlphaMTG.FlaUIInspector;

internal static class Program
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  [STAThread]
  public static int Main(string[] args)
  {
    var outputPath = GetOption(args, "--output")
      ?? Path.Combine("AlphaMTG", "FlaUIInspector", "mtgo-ui.json");
    var maxDepth = GetIntOption(args, "--max-depth") ?? 32;
    var rootSelector = GetOption(args, "--root") ?? "DuelSceneWindow";
    var verbose = HasFlag(args, "--verbose");
    JsonOptions.WriteIndented = !HasFlag(args, "--minified");

    try
    {
      var action = GetOption(args, "--action");
      if (action is not null)
      {
        var target = GetOption(args, "--target")
          ?? throw new ArgumentException("--target is required with --action.");
        var within = GetOption(args, "--within");
        var value = GetOption(args, "--value");
        var result = ExecuteAction(rootSelector, within, target, action, value);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
      }

      var snapshot = Capture(maxDepth, rootSelector, verbose);
      var fullOutputPath = Path.GetFullPath(outputPath);
      Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
      File.WriteAllText(fullOutputPath, JsonSerializer.Serialize(snapshot, JsonOptions));

      Console.WriteLine($"Wrote {snapshot.Windows.Count} MTGO window(s) rooted at '{rootSelector}' to {fullOutputPath}");
      Console.WriteLine($"Published {snapshot.Windows.Sum(window => window.ElementCount)} element(s) in {snapshot.Mode} mode.");
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine($"FlaUI capture failed: {exception}");
      return 1;
    }
  }

  private static ActionResult ExecuteAction(
    string rootSelector,
    string? withinSelector,
    string targetSelector,
    string action,
    string? value
  )
  {
    var processes = GetMtgoProcesses();
    using var automation = new UIA2Automation();

    foreach (var process in processes.Where(process => process.MainWindowHandle != 0))
    {
      var window = automation.FromHandle(new IntPtr(process.MainWindowHandle));
      var root = FindRoot(window, rootSelector);
      if (root is null)
        continue;

      var scope = withinSelector is null ? root : FindVisibleElement(root, withinSelector);
      if (scope is null)
        continue;

      var target = FindVisibleElement(scope, targetSelector);
      if (target is null)
        continue;

      if (!target.IsEnabled)
        throw new InvalidOperationException($"Target '{targetSelector}' is disabled.");

      var supportedActions = GetActions(target);
      if (!string.Equals(action, "inspect", StringComparison.OrdinalIgnoreCase))
      {
        if (!supportedActions.Contains(action, StringComparer.OrdinalIgnoreCase))
        {
          throw new InvalidOperationException(
            $"Target '{targetSelector}' does not support action '{action}'."
          );
        }

        PerformAction(target, action, value);
      }

      return new ActionResult(
        action,
        targetSelector,
        withinSelector,
        NullIfEmpty(target.AutomationId),
        NullIfEmpty(target.Name),
        supportedActions,
        true
      );
    }

    throw new InvalidOperationException(
      $"Could not find visible target '{targetSelector}'"
      + (withinSelector is null ? "." : $" within '{withinSelector}'.")
    );
  }

  private static void PerformAction(
    AutomationElement target,
    string action,
    string? value
  )
  {
    switch (action.ToLowerInvariant())
    {
      case "invoke":
        target.Patterns.Invoke.Pattern.Invoke();
        break;
      case "toggle":
        target.Patterns.Toggle.Pattern.Toggle();
        break;
      case "select":
        target.Patterns.SelectionItem.Pattern.Select();
        break;
      case "setvalue":
        target.Patterns.Value.Pattern.SetValue(
          value ?? throw new ArgumentException("--value is required for setValue.")
        );
        break;
      case "setrangevalue":
        if (!double.TryParse(value, out var number))
          throw new ArgumentException("A numeric --value is required for setRangeValue.");
        target.Patterns.RangeValue.Pattern.SetValue(number);
        break;
      case "expand":
        target.Patterns.ExpandCollapse.Pattern.Expand();
        break;
      case "collapse":
        target.Patterns.ExpandCollapse.Pattern.Collapse();
        break;
      case "click":
        Mouse.LeftClick(GetCenter(target));
        break;
      case "doubleclick":
        Mouse.LeftDoubleClick(GetCenter(target));
        break;
      case "rightclick":
        Mouse.RightClick(GetCenter(target));
        break;
      default:
        throw new ArgumentException($"Unsupported action '{action}'.");
    }
  }

  private static Point GetCenter(AutomationElement element)
  {
    var bounds = element.BoundingRectangle;
    if (bounds.Width <= 0 || bounds.Height <= 0)
      throw new InvalidOperationException("The target has no clickable bounds.");

    return new Point(
      bounds.X + bounds.Width / 2,
      bounds.Y + bounds.Height / 2
    );
  }

  private static Snapshot Capture(int maxDepth, string rootSelector, bool verbose)
  {
    var processes = GetMtgoProcesses();

    var processIds = processes.Select(process => process.Id).ToHashSet();
    using var automation = new UIA2Automation();
    var desktop = automation.GetDesktop();
    var windows = new List<WindowSnapshot>();
    var seenHandles = new HashSet<long>();

    foreach (var window in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
    {
      var processId = window.FrameworkAutomationElement.ProcessId;
      if (!processIds.Contains(processId))
        continue;

      AddWindow(windows, seenHandles, window, maxDepth, rootSelector, verbose);
    }

    // UIA2 can omit a top-level window from the desktop's direct children on
    // Wine. Fall back to the main HWND reported by each matching process.
    foreach (var process in processes)
    {
      if (process.MainWindowHandle == 0 || seenHandles.Contains(process.MainWindowHandle))
        continue;

      try
      {
        var window = automation.FromHandle(new IntPtr(process.MainWindowHandle));
        AddWindow(windows, seenHandles, window, maxDepth, rootSelector, verbose);
      }
      catch (Exception exception)
      {
        Console.Error.WriteLine(
          $"Unable to capture MTGO window {process.MainWindowHandle}: {exception.Message}"
        );
      }
    }

    return new Snapshot(
      DateTimeOffset.UtcNow,
      Environment.ProcessId,
      rootSelector,
      verbose ? "verbose" : "compact",
      processes,
      windows
    );
  }

  private static IReadOnlyList<ProcessInfo> GetMtgoProcesses()
  {
    return Process.GetProcesses()
      .Where(IsMtgoProcess)
      .Select(process =>
      {
        using (process)
        {
          return new ProcessInfo(
            process.Id,
            process.ProcessName,
            TryGetMainWindowTitle(process),
            process.MainWindowHandle.ToInt64()
          );
        }
      })
      .ToList();
  }

  private static void AddWindow(
    List<WindowSnapshot> windows,
    HashSet<long> seenHandles,
    AutomationElement window,
    int maxDepth,
    string rootSelector,
    bool verbose
  )
  {
    var handle = GetNativeWindowHandle(window);
    if (seenHandles.Contains(handle))
      return;

    var root = FindRoot(window, rootSelector);
    if (root is null)
      return;

    seenHandles.Add(handle);

    var capture = CaptureElementIndex(root, maxDepth, verbose);

    windows.Add(new WindowSnapshot(
      window.FrameworkAutomationElement.ProcessId,
      capture.Root,
      capture.Elements,
      capture.ObservedElementCount
    ));
  }

  private static AutomationElement? FindRoot(
    AutomationElement window,
    string rootSelector
  )
  {
    if (string.Equals(rootSelector, "all", StringComparison.OrdinalIgnoreCase)
      || string.Equals(rootSelector, "mtgo", StringComparison.OrdinalIgnoreCase))
      return window;

    if (Matches(window, rootSelector))
      return window;

    try
    {
      foreach (var descendant in window.FindAllDescendants())
      {
        if (Matches(descendant, rootSelector))
          return descendant;
      }
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(
        $"Unable to search MTGO window '{window.Name}' for '{rootSelector}': {exception.Message}"
      );
    }

    return null;
  }

  private static bool Matches(AutomationElement element, string selector)
  {
    return string.Equals(element.AutomationId, selector, StringComparison.Ordinal)
      || string.Equals(element.ClassName, selector, StringComparison.Ordinal)
      || string.Equals(element.Name, selector, StringComparison.Ordinal);
  }

  private static AutomationElement? FindVisibleElement(
    AutomationElement root,
    string selector
  )
  {
    if (Matches(root, selector) && IsVisible(root))
      return root;

    foreach (var descendant in root.FindAllDescendants())
    {
      if (Matches(descendant, selector) && IsVisible(descendant))
        return descendant;
    }

    return null;
  }

  private static ElementCapture CaptureElementIndex(
    AutomationElement root,
    int maxDepth,
    bool verbose
  )
  {
    var elements = new List<ElementSnapshot>();
    var observedElementCount = 0;
    IReadOnlyList<LocatorSegment> rootLocator = [CreateLocatorSegment(root)];
    var rootSnapshot = DescribeElement(root, rootLocator, GetActions(root));

    VisitChildren(
      root,
      rootLocator,
      maxDepth,
      verbose,
      ancestorsVisible: true,
      insideCardSurface: false,
      elements,
      ref observedElementCount
    );
    return new ElementCapture(rootSnapshot, elements, observedElementCount + 1);
  }

  private static void VisitChildren(
    AutomationElement parent,
    IReadOnlyList<LocatorSegment> parentLocator,
    int remainingDepth,
    bool verbose,
    bool ancestorsVisible,
    bool insideCardSurface,
    List<ElementSnapshot> elements,
    ref int observedElementCount
  )
  {
    if (remainingDepth <= 0)
      return;

    AutomationElement[] children;
    try
    {
      children = parent.FindAllChildren();
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(
        $"Unable to enumerate children of '{parent.AutomationId ?? parent.Name}': {exception.Message}"
      );
      return;
    }

    foreach (var child in children)
    {
      observedElementCount++;
      var childVisible = IsVisible(child) && ancestorsVisible;
      var actions = GetActions(child).ToList();
      AddCardActions(child, insideCardSurface, actions);
      var childLocator = parentLocator;
      if (verbose || childVisible && IsUseful(child, parentLocator.Count == 1, actions))
      {
        childLocator = [.. parentLocator, CreateLocatorSegment(child)];
        elements.Add(DescribeElement(child, childLocator, actions));
      }

      VisitChildren(
        child,
        childLocator,
        remainingDepth - 1,
        verbose,
        childVisible,
        insideCardSurface || IsCardSurface(child),
        elements,
        ref observedElementCount
      );
    }
  }

  private static bool IsCardSurface(AutomationElement element) =>
    string.Equals(element.ClassName, "Hand_View", StringComparison.Ordinal)
    || string.Equals(element.ClassName, "DuelBattlefieldView", StringComparison.Ordinal);

  private static void AddCardActions(
    AutomationElement element,
    bool insideCardSurface,
    List<string> actions
  )
  {
    if (!insideCardSurface
      || actions.Count > 0
      || !element.IsEnabled
      || !IsVisible(element)
      || element.ControlType is not (ControlType.Image or ControlType.Text)
      || string.IsNullOrWhiteSpace(element.AutomationId) && string.IsNullOrWhiteSpace(element.Name))
      return;

    actions.Add("click");
    actions.Add("doubleClick");
    actions.Add("rightClick");
  }

  private static bool IsVisible(AutomationElement element)
  {
    if (element.IsOffscreen)
      return false;

    try
    {
      var bounds = element.BoundingRectangle;
      return bounds.Width > 0 && bounds.Height > 0;
    }
    catch
    {
      return false;
    }
  }

  private static bool IsUseful(
    AutomationElement element,
    bool parentIsRoot,
    IReadOnlyList<string> actions
  )
  {
    // Images are decorative in MTGO's WPF UI and are not actionable. Keep
    // every other identified/named element, plus controls even when MTGO gives
    // them no AutomationId or Name.
    if (element.ControlType == ControlType.Image)
      return actions.Count > 0;

    if (!string.IsNullOrWhiteSpace(element.AutomationId)
      || !string.IsNullOrWhiteSpace(element.Name))
    {
      // Text directly under DuelSceneWindow is generally ambient chrome such
      // as the turn/phase timeline. Keep identified text, but require unnamed
      // text to belong to a semantic child component.
      return element.ControlType != ControlType.Text
        || !string.IsNullOrWhiteSpace(element.AutomationId)
        || !parentIsRoot;
    }

    return actions.Count > 0;
  }

  private static ElementSnapshot DescribeElement(
    AutomationElement element,
    IReadOnlyList<LocatorSegment> locator,
    IReadOnlyList<string> actions
  )
  {
    return new ElementSnapshot(
      locator,
      NullIfEmpty(element.AutomationId),
      NullIfEmpty(element.Name),
      NullIfEmpty(element.ClassName),
      element.ControlType.ToString(),
      element.IsEnabled,
      element.IsOffscreen,
      GetBounds(element),
      actions.Count == 0 ? null : actions
    );
  }

  private static LocatorSegment CreateLocatorSegment(AutomationElement element)
  {
    var automationId = NullIfEmpty(element.AutomationId);
    if (automationId is not null)
      return new LocatorSegment(automationId, null, null, null);

    var name = NullIfEmpty(element.Name);
    if (name is not null)
      return new LocatorSegment(null, name, null, element.ControlType.ToString());

    return new LocatorSegment(
      null,
      null,
      NullIfEmpty(element.ClassName),
      element.ControlType.ToString()
    );
  }

  private static IReadOnlyList<string> GetActions(AutomationElement element)
  {
    var actions = new List<string>();

    AddAction(actions, "invoke", () => element.Patterns.Invoke.IsSupported);
    AddAction(actions, "toggle", () => element.Patterns.Toggle.IsSupported);
    AddAction(actions, "select", () => element.Patterns.SelectionItem.IsSupported);
    AddAction(actions, "setValue", () => element.Patterns.Value.IsSupported);
    AddAction(actions, "setRangeValue", () => element.Patterns.RangeValue.IsSupported);
    if (Supports(() => element.Patterns.ExpandCollapse.IsSupported))
    {
      actions.Add("expand");
      actions.Add("collapse");
    }

    return actions;
  }

  private static void AddAction(
    List<string> actions,
    string action,
    Func<bool> isSupported
  )
  {
    try
    {
      if (isSupported())
        actions.Add(action);
    }
    catch
    {
      // Wine's UIA implementation can reject individual pattern probes. A
      // failed probe means that action is unavailable for this snapshot.
    }
  }

  private static bool Supports(Func<bool> isSupported)
  {
    try
    {
      return isSupported();
    }
    catch
    {
      return false;
    }
  }

  private static string? NullIfEmpty(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;

  private static Bounds? GetBounds(AutomationElement element)
  {
    try
    {
      var rectangle = element.BoundingRectangle;
      return new Bounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    }
    catch
    {
      return null;
    }
  }

  private static long GetNativeWindowHandle(AutomationElement element)
  {
    try
    {
      return Convert.ToInt64(element.FrameworkAutomationElement.NativeWindowHandle);
    }
    catch
    {
      // Most WPF descendants do not expose an HWND of their own.
      return 0;
    }
  }

  private static bool IsMtgoProcess(Process process)
  {
    try
    {
      return process.ProcessName.Contains("MTGO", StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
      return false;
    }
  }

  private static string? TryGetMainWindowTitle(Process process)
  {
    try
    {
      return process.MainWindowTitle;
    }
    catch
    {
      return null;
    }
  }

  private static string? GetOption(string[] args, string name)
  {
    for (var index = 0; index < args.Length - 1; index++)
    {
      if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        return args[index + 1];
    }

    return null;
  }

  private static int? GetIntOption(string[] args, string name)
  {
    return int.TryParse(GetOption(args, name), out var value) ? value : null;
  }

  private static bool HasFlag(string[] args, string name) =>
    args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

  private sealed record Snapshot(
    DateTimeOffset CapturedAtUtc,
    int InspectorProcessId,
    string RootSelector,
    string Mode,
    IReadOnlyList<ProcessInfo> Processes,
    IReadOnlyList<WindowSnapshot> Windows
  );

  private sealed record ProcessInfo(
    int Id,
    string Name,
    string? MainWindowTitle,
    long MainWindowHandle
  );

  private sealed record WindowSnapshot(
    int? ProcessId,
    ElementSnapshot Root,
    IReadOnlyList<ElementSnapshot> Elements,
    int ObservedElementCount
  )
  {
    [JsonIgnore]
    public int ElementCount => Elements.Count + 1;
  }

  private sealed record ElementSnapshot(
    IReadOnlyList<LocatorSegment> Locator,
    string? AutomationId,
    string? Name,
    string? ClassName,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    Bounds? Bounds,
    IReadOnlyList<string>? Actions
  );

  private sealed record LocatorSegment(
    string? AutomationId,
    string? Name,
    string? ClassName,
    string? ControlType
  );

  private sealed record Bounds(
    int X,
    int Y,
    int Width,
    int Height
  );

  private sealed record ElementCapture(
    ElementSnapshot Root,
    IReadOnlyList<ElementSnapshot> Elements,
    int ObservedElementCount
  );

  private sealed record ActionResult(
    string Action,
    string Target,
    string? Within,
    string? AutomationId,
    string? Name,
    IReadOnlyList<string> SupportedActions,
    bool Success
  );
}
