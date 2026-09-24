using System.Diagnostics;

using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;

namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// Signals when MTGO's UIA tree reports a relevant UI transition.
/// </summary>
/// <remarks>
/// UIA events are used as wake-up hints. The caller still checks the actual
/// condition because a provider can report an event before a transient child
/// is fully populated, or can omit an event entirely under Wine.
/// </remarks>
internal sealed class UIAEventPoller : IDisposable
{
  private const int PollMilliseconds = 25;

  private readonly ManualResetEventSlim _changed = new();
  private readonly List<IDisposable> _subscriptions = [];

  public UIAEventPoller(AutomationBase automation, AutomationElement element)
  {
    var desktop = automation.GetDesktop();
    var events = automation.EventLibrary.Element;
    var properties = automation.PropertyLibrary.Element;

    TryAdd(() => element.RegisterStructureChangedEvent(
      TreeScope.Subtree,
      OnStructureChanged
    ));
    TryAdd(() => desktop.RegisterStructureChangedEvent(
      TreeScope.Subtree,
      OnStructureChanged
    ));

    TryAdd(() => element.RegisterAutomationEvent(
      events.MenuOpenedEvent,
      TreeScope.Subtree,
      OnAutomationEvent
    ));
    TryAdd(() => element.RegisterAutomationEvent(
      events.MenuClosedEvent,
      TreeScope.Subtree,
      OnAutomationEvent
    ));
    TryAdd(() => desktop.RegisterAutomationEvent(
      events.MenuOpenedEvent,
      TreeScope.Subtree,
      OnAutomationEvent
    ));
    TryAdd(() => desktop.RegisterAutomationEvent(
      events.MenuClosedEvent,
      TreeScope.Subtree,
      OnAutomationEvent
    ));

    TryAdd(() => element.RegisterPropertyChangedEvent(
      TreeScope.Subtree,
      OnPropertyChanged,
      properties.IsEnabled,
      properties.IsOffscreen,
      properties.Name,
      properties.BoundingRectangle
    ));
  }

  public bool WaitForChange(TimeSpan timeout)
  {
    bool changed = _changed.Wait(timeout);
    if (changed)
      _changed.Reset();
    return changed;
  }

  public bool WaitFor(Func<bool> probe, TimeSpan timeout)
  {
    var stopwatch = Stopwatch.StartNew();

    while (stopwatch.Elapsed < timeout)
    {
      if (TryProbe(probe))
        return true;

      if (!_changed.Wait(PollMilliseconds))
        continue;

      // Consume the wake-up before checking the condition again. Several
      // providers can report more than one event for one logical transition.
      _changed.Reset();
    }

    return TryProbe(probe);
  }

  private void TryAdd(Func<IDisposable> register)
  {
    try
    {
      _subscriptions.Add(register());
    }
    catch (Exception)
    {
      // UIA2 providers do not all implement every event type. Polling remains
      // the fallback when a provider rejects one of these subscriptions.
    }
  }

  private static bool TryProbe(Func<bool> probe)
  {
    try
    {
      return probe();
    }
    catch (Exception)
    {
      // A popup can be removed between the provider exposing it and this
      // process querying it. Treat that as "not ready yet" during the bounded
      // wait; the final capture will still surface a persistent failure.
      return false;
    }
  }

  private void OnStructureChanged(
    AutomationElement _sender,
    StructureChangeType _changeType,
    int[] _runtimeId
  ) => _changed.Set();

  private void OnAutomationEvent(AutomationElement _sender, EventId _eventId) =>
    _changed.Set();

  private void OnPropertyChanged(
    AutomationElement _sender,
    PropertyId _propertyId,
    object _newValue
  ) => _changed.Set();

  public void Dispose()
  {
    foreach (var subscription in _subscriptions)
    {
      try
      {
        subscription.Dispose();
      }
      catch (Exception)
      {
        // The underlying UIA provider may already have torn down the element.
      }
    }

    _changed.Dispose();
  }
}
