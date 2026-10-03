/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using MouseButton = System.Windows.Input.MouseButton;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.Core.Reflection;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Interop;
using MTGOSDK.Core.Remoting.Types;
using AlphaMTG.MTGOAgent.UIA;

namespace AlphaMTG.MTGOAgent.UIA.Actions;

/// <summary>
/// Uniform SELECT for every mode: hit-test, well-formed routed args, then
/// MTGO's own handlers. No cursor moves, no SendInput, no host calls.
/// </summary>
/// <remarks>
/// Resolution is visual (topmost wins, like a real cursor); actuation is
/// MTGO's unmodified OnMouseDown/OnMouseUp pipeline. Occluded targets
/// resolve to the covering visual and are refused, never clicked through.
/// </remarks>
internal sealed record SelectionResult(
  bool Resolved,
  string HitType,
  string Detail);

internal static class SelectionClick
{
  public static SelectionResult Select(
    double x,
    double y,
    int anchorCardId,
    MTGOSDK.API.Play.Games.Game game,
    Action<string>? log = null)
  {
    return SelectButton(
      x, y, anchorCardId, game,
      System.Windows.Input.MouseButton.Left, log);
  }

  private static SelectionResult SelectButton(
    double x,
    double y,
    int anchorCardId,
    MTGOSDK.API.Play.Games.Game game,
    System.Windows.Input.MouseButton button,
    Action<string>? log)
  {
    void Log(string message)
    {
      if (log is not null) log(message);
    }

    // The server holds the single client for the process lifetime; no
    // per-operation attach here. (A second attach mid-operation used to
    // poison the shared static remote state when disposed.)
    return SelectCore(x, y, anchorCardId, game, button, Log);
  }

  private static SelectionResult SelectCore(
    double x,
    double y,
    int anchorCardId,
    MTGOSDK.API.Play.Games.Game game,
    System.Windows.Input.MouseButton button,
    Action<string> Log)
  {
    using (DiverCommunicator.BeginUIThreadScope())
    {
      var activator = new RemoteActivator(
        RemoteClient.client.Communicator, RemoteClient.client);

      // MTGO hosts the duel in a separate top-level window from the
      // shell, so hit-test every window and keep the one that resolves
      // to a selectable visual.
      dynamic app = RemoteClient.InvokeMethod(
        "System.Windows.Application", "get_Current", null);
      dynamic windows = app.Windows;
      int windowCount = (int)windows.Count;

      // Raise on the deepest hit visual, NOT an ancestor: MTGO's
      // ClickedInteractableItem walks visual parents excluding self, so
      // raising on an ancestor itself resolves to nothing.
      dynamic raiseTarget = null;
      string hitType = "?";
      string targetType = "?";
      for (int w = 0; w < windowCount; w++)
      {
        dynamic window = windows[w];
        dynamic screenPoint = activator.CreateInstance(
          "WindowsBase",
          "System.Windows.Point", x, y).Dynamify();
        dynamic clientPoint = window.PointFromScreen(screenPoint);
        dynamic hitVisual = window.InputHitTest(clientPoint);
        if (hitVisual is null)
          continue;
        hitType = ((object)hitVisual is DynamicRemoteObject hitDro)
          ? hitDro.__type?.FullName ?? "?"
          : "?";

        dynamic candidate = hitVisual;
        bool accepted = false;
        for (int depth = 0; depth < 24; depth++)
        {
          string candidateType =
            ((object)candidate is DynamicRemoteObject candidateDro)
              ? candidateDro.__type?.FullName ?? "?"
              : "?";
          if (candidateType.Contains("Card_View")
            || candidateType.Contains("Avatar")
            || candidateType.Contains("Player"))
          {
            accepted = true;
            targetType = candidateType;
            break;
          }
          dynamic parent = null;
          try { parent = candidate.Parent; }
          catch { }
          if (parent is null)
          {
            try { parent = candidate.TemplatedParent; }
            catch { }
          }
          if (parent is null)
            break;
          candidate = parent;
        }
        if (accepted)
        {
          raiseTarget = hitVisual;
          break;
        }
      }

      if (raiseTarget is null)
      {
        return new SelectionResult(false, hitType, "No selectable visual hit.");
      }
      Log($"target: {targetType}, raising on {hitType}.");

      // Fabricate down/up with a real device; same dispatch as hardware.
      dynamic device = RemoteClient.InvokeMethod(
        "System.Windows.Input.Mouse", "get_PrimaryDevice", null);
      int tick = Environment.TickCount;
      dynamic downArgs = activator.CreateInstance(
        "PresentationCore",
        "System.Windows.Input.MouseButtonEventArgs",
        device, tick, button).Dynamify();
      dynamic upArgs = activator.CreateInstance(
        "PresentationCore",
        "System.Windows.Input.MouseButtonEventArgs",
        device, tick, button).Dynamify();

      Type uiElement = RemoteClient.GetInstanceType("System.Windows.UIElement");
      dynamic previewDownEvent = uiElement
        .GetField("PreviewMouseLeftButtonDownEvent", (BindingFlags) 0xffff)
        .GetValue(null);
      dynamic downEvent = uiElement
        .GetField("MouseLeftButtonDownEvent", (BindingFlags) 0xffff)
        .GetValue(null);
      dynamic previewUpEvent = uiElement
        .GetField("PreviewMouseLeftButtonUpEvent", (BindingFlags) 0xffff)
        .GetValue(null);
      dynamic upEvent = uiElement
        .GetField("MouseLeftButtonUpEvent", (BindingFlags) 0xffff)
        .GetValue(null);
      dynamic previewDownArgs = activator.CreateInstance(
        "PresentationCore",
        "System.Windows.Input.MouseButtonEventArgs",
        device, tick, button).Dynamify();
      dynamic previewUpArgs = activator.CreateInstance(
        "PresentationCore",
        "System.Windows.Input.MouseButtonEventArgs",
        device, tick, button).Dynamify();
      previewDownArgs.RoutedEvent = previewDownEvent;
      downArgs.RoutedEvent = downEvent;
      previewUpArgs.RoutedEvent = previewUpEvent;
      upArgs.RoutedEvent = upEvent;

      raiseTarget.RaiseEvent(previewDownArgs);
      raiseTarget.RaiseEvent(downArgs);
      raiseTarget.RaiseEvent(previewUpArgs);
      raiseTarget.RaiseEvent(upArgs);

      // Double-click pair for zone viewers (left button only; a right
      // double-click has no meaning in MTGO).
      if (button == MouseButton.Left)
      {
        Type controlElement =
          RemoteClient.GetInstanceType("System.Windows.Controls.Control");
        dynamic previewDoubleEvent = controlElement
          .GetField("PreviewMouseDoubleClickEvent", (BindingFlags) 0xffff)
          .GetValue(null);
        dynamic doubleEvent = controlElement
          .GetField("MouseDoubleClickEvent", (BindingFlags) 0xffff)
          .GetValue(null);
        if (!(previewDoubleEvent is null) && !(doubleEvent is null))
        {
          dynamic doubleArgs = activator.CreateInstance(
            "PresentationCore",
            "System.Windows.Input.MouseButtonEventArgs",
            device, tick, button).Dynamify();
          doubleArgs.RoutedEvent = previewDoubleEvent;
          raiseTarget.RaiseEvent(doubleArgs);
          doubleArgs.RoutedEvent = doubleEvent;
          raiseTarget.RaiseEvent(doubleArgs);
        }
      }

      // Direct handler invocation with the same well-formed args: no
      // routing, no Handled interference. Needs OriginalSource set.
      downArgs.Source = raiseTarget;
      upArgs.Source = raiseTarget;
      var downRo = ((DynamicRemoteObject)(object)downArgs).__ro;
      var upRo = ((DynamicRemoteObject)(object)upArgs).__ro;
      var visualDro =
        (DynamicRemoteObject)(object)Unbind(raiseTarget);
      var visualRef =
        MTGOSDK.Core.Remoting.Interop.ObjectOrRemoteAddress.FromToken(
          visualDro.__ro.RemoteToken,
          visualDro.__type?.FullName ?? "System.Windows.DependencyObject");
      downRo.SetField("_originalSource", visualRef);
      upRo.SetField("_originalSource", visualRef);

      dynamic? scene = ResolveScene(game, anchorCardId);
      if (scene is null)
      {
        return new SelectionResult(
          false, hitType, "No scene resolved for direct invoke.");
      }
      try
      {
        var sceneDro = (DynamicRemoteObject)(object)scene;
        Log($"scene refs: ro={(sceneDro.__ro is null ? "null" : "ok")} "
          + $"type={(sceneDro.__type is null ? "null" : sceneDro.__type.FullName)} "
          + $"ra={(sceneDro.__ra is null ? "null" : "ok")}");
        var argsDro = (DynamicRemoteObject)(object)downArgs;
        Log($"args refs: ro={(argsDro.__ro is null ? "null" : "ok")} "
          + $"type={(argsDro.__type is null ? "null" : argsDro.__type.FullName)}");
      }
      catch (Exception ex)
      {
        Log($"ref check failed: {ex.Message.Split('\n')[0]}");
      }
      scene.OnMouseDown(null, downArgs);
      scene.OnMouseUp(null, upArgs, false);

      string state = SnapshotScene(scene);
      return new SelectionResult(true, hitType, $"Selected. {state}");
    }
  }

  private static dynamic? ResolveScene(
    MTGOSDK.API.Play.Games.Game game, int anchorCardId)
  {
    // Mirrors CardActionRegistry's scene resolution shapes exactly so
    // Unbind overloads resolve (SDK-typed, never raw dynamic).
    try
    {
      MTGOSDK.API.Play.Games.GameCard? sdkCard =
        game.GetGameCard(anchorCardId);
      if (sdkCard is null)
        return null;
      dynamic anchor = Unbind(sdkCard);
      dynamic? cardVM = anchor?.Tag?.CardViewModel;
      if (cardVM is null)
        return null;
      dynamic? mainViewModel = cardVM.MainViewModel;
      if (mainViewModel is null)
        return null;
      return Unbind(mainViewModel);
    }
    catch
    {
      return null;
    }
  }

  private static string SnapshotScene(dynamic scene)
  {
    string startTime = "?";
    try { startTime = scene.InteractionState.StartTime.ToString(); }
    catch { }
    string mode = "?";
    try { mode = scene.InteractionState.Mode.ToString(); }
    catch { }
    return $"startTime={startTime} mode={mode}";
  }
}
