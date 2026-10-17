/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Windows;
using System.Windows.Media;

using MTGOSDK.API.Interface.ViewModels;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Batching;
using MTGOSDK.Core.Remoting.Interop;
using MTGOSDK.Core.Remoting.Interop.Interactions.Batching;

using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

namespace AlphaMTG.MTGOAgent.Controllers.UIA;

/// <summary>
/// Resolves clipping and scrolling from the card's actual visual ancestors.
/// Docked, expanded, and floating zone views can have different parents.
/// </summary>
internal sealed record SelectionViewport(
  Rectangle Bounds, IReadOnlyList<Rectangle> Controls, object? ScrollView)
{
  internal static SelectionViewport ForCard(DuelSceneViewModel duel, int cardId)
  {
    using var scope = DiverCommunicator.BeginUIThreadScope();
    dynamic scene = Unbind(duel);
    dynamic view = scene.Game.FindGameCard(cardId).Tag.CardViewModel.m_battlefieldPositionView
      ?? throw new InvalidOperationException($"Card {cardId} has no loaded view.");

    dynamic window = RemoteClient.InvokeMethod(typeof(Window).FullName!,
      nameof(Window.GetWindow), null, view);
    Rectangle bounds = ReadBounds((object)window);
    var controls = new List<Rectangle>();
    object? scrollView = null;
    for (dynamic? parent = view; parent is not null;
      parent = RemoteClient.InvokeMethod(typeof(VisualTreeHelper).FullName!,
        nameof(VisualTreeHelper.GetParent), null, parent))
    {
      string type = parent.GetType().FullName;
      if (type == "Shiny.Play.Duel.VerticalPanelView")
      {
        scrollView = (object)parent;
        if ((bool)parent.UpArrow.IsVisible) controls.Add(ReadBounds((object)parent.UpArrow));
        if ((bool)parent.DownArrow.IsVisible) controls.Add(ReadBounds((object)parent.DownArrow));
      }

      // WPF clips these lists at VerticalPanelView, not at the card's
      // immediate drawing panel. Include outer clipping and the owning window.
      if ((bool)parent.ClipToBounds)
      {
        Rectangle clip = ReadBounds((object)parent);
        bounds = Rectangle.Intersect(bounds, clip);
      }
    }

    return new(bounds, controls, scrollView);
  }

  internal bool Scroll(bool down)
  {
    if (ScrollView is null) return false;
    using var scope = DiverCommunicator.BeginUIThreadScope();
    dynamic panel = ((dynamic)ScrollView).m_vertPanel;
    if (down)
    {
      if (!(bool)panel.CanScrollDown) return false;
      // MTGO's DownArrow handler calls ScrollUp (negative content offset).
      panel.ScrollUp();
    }
    else
    {
      if (!(bool)panel.CanScrollUp) return false;
      panel.ScrollDown();
    }
    return true;
  }

  private static Rectangle ReadBounds(object element)
  {
    var batch = new RemoteBatch { ForceUIThread = true };
    var view = batch.Input(element);
    var zero = batch.Input(0d);
    var width = batch.Read(view, "ActualWidth");
    var height = batch.Read(view, "ActualHeight");
    var projection = RemoteBatchProjection.Object(
      ("X", "X", RemoteBatchProjection.Value()),
      ("Y", "Y", RemoteBatchProjection.Value()));
    foreach (var (name, x, y) in new[] {
      ("tl", zero, zero), ("tr", width, zero),
      ("bl", zero, height), ("br", width, height)
    })
    {
      var point = batch.Create(Method(typeof(Point), ".ctor", typeof(double), typeof(double)), x, y);
      batch.Output(name, batch.Call(view,
        Method(typeof(Visual), nameof(Visual.PointToScreen), typeof(Point)), point), projection);
    }
    var values = batch.Execute(RemoteClient.client).Values.Values
      .Select(value => value.Members).ToArray();
    return Rectangle.FromLTRB(
      (int)Math.Ceiling(values.Min(point => point["X"].GetScalar<double>())),
      (int)Math.Ceiling(values.Min(point => point["Y"].GetScalar<double>())),
      (int)Math.Floor(values.Max(point => point["X"].GetScalar<double>())),
      (int)Math.Floor(values.Max(point => point["Y"].GetScalar<double>())));
  }

  private static RemoteBatchMethod Method(Type owner, string name, params Type[] parameters) =>
    new(RemoteBatchType.From(owner), name, parameters.Select(RemoteBatchType.From).ToArray());
}
