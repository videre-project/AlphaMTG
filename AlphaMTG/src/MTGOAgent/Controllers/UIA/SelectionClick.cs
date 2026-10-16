/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

using MTGOSDK.API.Interface.ViewModels;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Batching;
using MTGOSDK.Core.Remoting.Interop;
using MTGOSDK.Core.Remoting.Interop.Interactions.Batching;


namespace AlphaMTG.MTGOAgent.Controllers.UIA;

/// <summary>
/// Dispatches selection through MTGO's WPF input and scene handlers.
/// </summary>
internal static class SelectionClick
{
  /// <summary>
  /// Resolves the window containing this game's duel for UI Automation queries.
  /// </summary>
  public static IntPtr GetWindowHandle(DuelSceneViewModel scene)
  {
    var batch = new RemoteBatch { ForceUIThread = true };
    var root = batch.Read(batch.Input((object)Unbind(scene)), "DuelSceneRoot");
    var window = batch.CallStatic(Method(typeof(Window), nameof(Window.GetWindow),
      typeof(DependencyObject)), root);
    var helper = batch.Create(Method(typeof(WindowInteropHelper), ".ctor", typeof(Window)), window);
    var handle = batch.Read(helper, nameof(WindowInteropHelper.Handle));
    batch.Output("handle", batch.Call(handle, Method(typeof(IntPtr), nameof(IntPtr.ToInt64))));
    return new IntPtr(batch.Execute(RemoteClient.client).Values["handle"].GetScalar<long>());
  }

  public static void Select(
    double x, double y, DuelSceneViewModel duel, int? expectedCardId = null)
  {
    dynamic scene = Unbind(duel);
    object visual;
    using (DiverCommunicator.BeginUIThreadScope())
    {
      // Hit-test the owning window so coordinates and occluding visuals use
      // the same WPF surface as the geometry supplied by the SDK.
      dynamic window = RemoteClient.InvokeMethod(
        "System.Windows.Window", nameof(Window.GetWindow), null, scene.DuelSceneRoot);
      var activator = new RemoteActivator(RemoteClient.client.Communicator, RemoteClient.client);
      dynamic screenPoint = activator.CreateInstance(
        "WindowsBase", "System.Windows.Point", x, y).Dynamify();
      dynamic? hit = window.InputHitTest(window.PointFromScreen(screenPoint));
      if (hit is null)
        throw new InvalidOperationException($"No visual under ({x},{y}).");
      visual = (object)hit;
      dynamic? item = scene.ClickedInteractableItem(hit);
      if (item is null)
        throw new InvalidOperationException($"No selectable visual under ({x},{y}).");

      if (expectedCardId.HasValue)
      {
        var check = new RemoteBatch { ForceUIThread = true };
        check.Output("cardId", check.Read(check.Input((object)item), "DataContext.GameCard.Id"));
        var value = check.Execute(RemoteClient.client).Values["cardId"];
        if (value.Kind == RemoteBatchValueKind.Null)
          throw new InvalidOperationException($"The visual under ({x},{y}) is not a card.");
        int hitCardId = value.GetScalar<int>();
        if (hitCardId != expectedCardId.Value)
          throw new InvalidOperationException($"Hit card {hitCardId}, expected card {expectedCardId.Value}.");
      }
    }

    CreateClickBatch((object)scene, visual).Execute(RemoteClient.client);
  }

  private static RemoteBatch CreateClickBatch(object scene, object visual)
  {
    var batch = new RemoteBatch { ForceUIThread = true };
    var target = batch.Input(visual);
    var device = batch.CallStatic(Method(typeof(Mouse), "get_PrimaryDevice"));
    var tick = batch.Input(Environment.TickCount);
    var button = batch.Input(MouseButton.Left);

    // Keep the routed events used by card and zone controls. Their arguments
    // remain in the target process rather than being pinned after each call.
    foreach (string name in new[]
    {
      nameof(UIElement.PreviewMouseLeftButtonDownEvent),
      nameof(UIElement.MouseLeftButtonDownEvent),
      nameof(UIElement.PreviewMouseLeftButtonUpEvent),
      nameof(UIElement.MouseLeftButtonUpEvent)
    })
    {
      var routedEvent = ReadEvent(batch, typeof(UIElement), name);
      var args = MouseEvent(batch, device, tick, button, target, routedEvent);
      batch.Call(target, Method(typeof(UIElement), nameof(UIElement.RaiseEvent), typeof(RoutedEventArgs)), args);
    }

    var doubleArgs = MouseEvent(batch, device, tick, button, target,
      ReadEvent(batch, typeof(Control), nameof(Control.PreviewMouseDoubleClickEvent)));
    foreach (string name in new[]
    {
      nameof(Control.PreviewMouseDoubleClickEvent),
      nameof(Control.MouseDoubleClickEvent)
    })
    {
      var routedEvent = ReadEvent(batch, typeof(Control), name);
      batch.Call(doubleArgs,
        Method(typeof(RoutedEventArgs), "set_RoutedEvent", typeof(RoutedEvent)), routedEvent);
      batch.Call(target,
        Method(typeof(UIElement), nameof(UIElement.RaiseEvent), typeof(RoutedEventArgs)), doubleArgs);
    }

    // Fresh arguments retain the hit visual as OriginalSource through WPF's
    // Source setter, without rewriting RoutedEventArgs' private fields.
    var down = MouseEvent(batch, device, tick, button, target,
      ReadEvent(batch, typeof(UIElement), nameof(UIElement.MouseLeftButtonDownEvent)));
    var up = MouseEvent(batch, device, tick, button, target,
      ReadEvent(batch, typeof(UIElement), nameof(UIElement.MouseLeftButtonUpEvent)));
    var model = batch.Input(scene);
    var sceneType = new RemoteBatchType(
      "Shiny.Play.Duel.ViewModel.DuelSceneViewModel", "DuelScene");
    batch.Call(model, new(sceneType, "OnMouseDown",
      RemoteBatchType.From(typeof(object)), RemoteBatchType.From(typeof(MouseButtonEventArgs))),
      batch.Input(ObjectOrRemoteAddress.Null), down);
    batch.Call(model, new(sceneType, "OnMouseUp",
      RemoteBatchType.From(typeof(object)), RemoteBatchType.From(typeof(MouseButtonEventArgs)),
      RemoteBatchType.From(typeof(bool))), batch.Input(ObjectOrRemoteAddress.Null), up, batch.Input(false));
    return batch;
  }

  private static RemoteBatchSlot MouseEvent(
    RemoteBatch batch, RemoteBatchSlot device, RemoteBatchSlot tick,
    RemoteBatchSlot button, RemoteBatchSlot source, RemoteBatchSlot routedEvent)
  {
    var args = batch.Create(Method(typeof(MouseButtonEventArgs), ".ctor",
      typeof(MouseDevice), typeof(int), typeof(MouseButton)), device, tick, button);
    batch.Call(args, Method(typeof(RoutedEventArgs), "set_RoutedEvent", typeof(RoutedEvent)), routedEvent);
    batch.Call(args, Method(typeof(RoutedEventArgs), "set_Source", typeof(object)), source);
    return args;
  }

  private static RemoteBatchSlot ReadEvent(RemoteBatch batch, Type owner, string name)
  {
    var field = batch.Call(batch.Input(ObjectOrRemoteAddress.FromType(owner)),
      Method(typeof(Type), nameof(Type.GetField), typeof(string), typeof(BindingFlags)),
      batch.Input(name), batch.Input(BindingFlags.Public | BindingFlags.Static));
    return batch.Call(field, Method(typeof(FieldInfo), nameof(FieldInfo.GetValue), typeof(object)),
      batch.Input(ObjectOrRemoteAddress.Null));
  }

  private static RemoteBatchMethod Method(Type owner, string name, params Type[] parameters) =>
    new(RemoteBatchType.From(owner), name, parameters.Select(RemoteBatchType.From).ToArray());
}
