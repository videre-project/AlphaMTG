using FlaUI.UIA2;

using Microsoft.Extensions.Logging;

namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// Wakes the agent when MTGO's UIA tree changes.
/// </summary>
/// <remarks>
/// UIA events are notifications, not state commits. The callback must validate
/// the current semantic state before exporting it. A periodic probe remains as
/// a bounded fallback because Wine's UIA2 provider can omit events.
/// </remarks>
internal sealed class UIActivityMonitor : IDisposable
{
  private static readonly TimeSpan EventWait = TimeSpan.FromMilliseconds(250);
  private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(1);

  private readonly ILogger _logger;
  private readonly Action _onChanged;
  private readonly CancellationTokenSource _cancellation = new();
  private readonly Task _task;

  public UIActivityMonitor(ILogger logger, Action onChanged)
  {
    _logger = logger;
    _onChanged = onChanged;
    _task = Task.Run(Run);
  }

  private void Run()
  {
    try
    {
      using var automation = new UIA2Automation();
      using var poller = new UIAEventPoller(
        automation,
        automation.GetDesktop());
      DateTime nextFallback = DateTime.UtcNow + FallbackInterval;

      while (!_cancellation.IsCancellationRequested)
      {
        bool changed = poller.WaitForChange(EventWait);
        if (!changed && DateTime.UtcNow < nextFallback)
          continue;

        nextFallback = DateTime.UtcNow + FallbackInterval;
        _onChanged();
      }
    }
    catch (Exception exception) when (!_cancellation.IsCancellationRequested)
    {
      _logger.LogWarning(exception, "MTGO UIA monitor stopped unexpectedly.");
    }
  }

  public void Dispose()
  {
    _cancellation.Cancel();
    try
    {
      _task.Wait(TimeSpan.FromSeconds(2));
    }
    catch (AggregateException exception)
    {
      _logger.LogDebug(
        exception.Flatten().InnerException,
        "MTGO UIA monitor completed with an exception.");
    }
    finally
    {
      _cancellation.Dispose();
    }
  }
}
