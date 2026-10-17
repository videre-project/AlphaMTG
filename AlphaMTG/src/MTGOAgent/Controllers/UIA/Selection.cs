/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;

using FlaUI.Core.AutomationElements;

using MTGOSDK.Core.Diagnostics;
using MTGOSDK.API.Interface.ViewModels;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors.Events;

using CardPosition = MTGOSDK.API.Interface.ViewModels.CardViewModel.Position;


namespace AlphaMTG.MTGOAgent.Controllers.UIA;

/// <summary>
/// Locates card and player click areas and dispatches selection to MTGO.
/// </summary>
internal sealed class Selection(Game game, GameStateChangedEventArgs update) : IDisposable
{
  private readonly Game _game = game;
  private readonly GameStateChangedEventArgs _update = update;
  private readonly Lazy<UIASession> _session = new(() => new UIASession());

  public void Dispose()
  {
    if (_session.IsValueCreated) _session.Value.Dispose();
  }

  private DuelSceneViewModel Scene => DuelSceneViewModel.ForGame(_game)
    ?? throw new InvalidOperationException("The game has no loaded duel scene.");

  /// <summary>
  /// Clicks an uncovered point in the card geometry captured by MTGOSDK.
  /// </summary>
  internal Point SelectCard(GameCard card)
  {
    using var timing = LocalTelemetry.Begin("alpha.select.card");
    DuelSceneViewModel scene = Scene;
    int cardId = card.Id;
    Point point = GetClickArea(card, scene).ClickPoint
      ?? throw new InvalidOperationException(
        $"Card {cardId} has no safe uncovered area to click.");

    using (LocalTelemetry.Begin("alpha.click", "card"))
      SelectionClick.Select(point.X, point.Y, scene, expectedCardId: cardId);
    return point;
  }

  /// <summary>
  /// Finds the player's mat through UI Automation and clicks its center.
  /// MTGOSDK's current geometry API supplies positions for card views.
  /// </summary>
  internal void SelectPlayer(string playerName)
  {
    using var timing = LocalTelemetry.Begin("alpha.select.player");
    DuelSceneViewModel scene = Scene;
    IntPtr windowHandle = SelectionClick.GetWindowHandle(scene);
    AutomationElement element;
    using (LocalTelemetry.Begin("alpha.player.lookup"))
      element = _session.Value.FindPlayerBox(playerName, windowHandle)
        ?? throw new InvalidOperationException(
          $"No player box for '{playerName}' in the duel scene.");

    if (!UIASession.GetIsEnabled(element))
    {
      throw new InvalidOperationException(
        $"The player box for '{playerName}' is disabled.");
    }

    Rectangle box = UIASession.GetBounds(element)
      ?? throw new InvalidOperationException(
        $"Player box for '{playerName}' has no bounds.");
    using (LocalTelemetry.Begin("alpha.click", "player"))
      SelectionClick.Select(box.X + box.Width / 2, box.Y + box.Height / 2, scene);
  }

  /// <summary>
  /// Chooses the largest area left after subtracting cards drawn in front.
  /// Cards in different panels are treated conservatively as possible occluders.
  /// </summary>
  internal (
    Rectangle Target, IReadOnlyList<Rectangle> Occluders,
    IReadOnlyList<Rectangle> VisibleAreas, Point? ClickPoint,
    IReadOnlyDictionary<int, CardPosition> Positions) GetClickArea(
      GameCard card, DuelSceneViewModel? scene = null)
  {
    using var timing = LocalTelemetry.Begin("alpha.card.geometry");
    scene ??= Scene;
    string? zone = card.Zone?.Name;

    var ids = _update.Snapshot.Cards.Values.Concat(_update.Snapshot.HiddenCards.Values)
      .Where(candidate => candidate.Zone?.Name == zone)
      .Select(candidate => candidate.Id)
      .ToList();
    for (int attempts = 0; ; attempts++)
    {
      SelectionViewport viewport = SelectionViewport.ForCard(scene, card.Id);
      var positions = scene.GetCardPositions(ids);
      CardPosition placement = positions.GetValueOrDefault(card.Id)
        ?? throw new InvalidOperationException($"Card {card.Id} has no live screen position.");
      var position = placement.Bounds;
      var target = new Rectangle(position.X, position.Y, position.Width, position.Height);
      var occluders = positions.Where(pair => pair.Key != card.Id)
        .Select(pair => pair.Value)
        .Where(other => other.PanelIndex != placement.PanelIndex
          || other.ZIndex > placement.ZIndex
          || (other.ZIndex == placement.ZIndex && other.ChildIndex > placement.ChildIndex))
        .Select(other => other.Bounds)
        .Select(point => new Rectangle(point.X, point.Y, point.Width, point.Height))
        .Concat(viewport.Controls)
        .Where(rect => rect.Width > 0 && rect.Height > 0)
        .Where(rect => rect.IntersectsWith(target))
        .Distinct()
        .ToList();

      // A partially visible card is clickable; its top-left corner need not
      // reach the middle of the list. Never choose pixels outside its viewport.
      Rectangle clipped = Rectangle.Intersect(target, viewport.Bounds);
      List<Rectangle> visible = clipped.IsEmpty ? [] : UncoveredRegions(clipped, occluders);
      Point? point = SafePoint(visible);
      if (point.HasValue) return (target, occluders, visible, point, positions);

      bool below = target.Bottom > viewport.Bounds.Bottom;
      if (attempts == 10 || !viewport.Scroll(below))
        throw new InvalidOperationException(
          $"Card {card.Id} has no uncovered click area in its containing viewport "
            + $"{viewport.Bounds} (card bounds {target}).");
      Thread.Sleep(400);
    }
  }

  private static List<Rectangle> UncoveredRegions(
    Rectangle target,
    IEnumerable<Rectangle> occluders)
  {
    var visible = new List<Rectangle> { target };
    foreach (Rectangle cut in occluders)
    {
      visible = visible.SelectMany(region => Subtract(region, cut)).ToList();
      if (visible.Count == 0) break;
    }
    return visible;
  }

  /// <summary>
  /// Chooses the center of the largest exposed region. The eight-pixel minimum
  /// excludes rounding artifacts while allowing narrow strips in card stacks.
  /// </summary>
  private static Point? SafePoint(IEnumerable<Rectangle> regions)
  {
    Rectangle best = regions
      .Where(region => region.Width >= 8 && region.Height >= 8)
      .OrderByDescending(region => (long)region.Width * region.Height)
      .FirstOrDefault();
    return best.IsEmpty
      ? null
      : new Point(best.X + best.Width / 2, best.Y + best.Height / 2);
  }

  /// <summary>
  /// Splits <paramref name="target"/> around <paramref name="cut"/>,
  /// returning the surviving rectangles.
  /// </summary>
  private static IReadOnlyList<Rectangle> Subtract(Rectangle target, Rectangle cut)
  {
    Rectangle intersection = Rectangle.Intersect(target, cut);
    if (intersection.IsEmpty) return [target];

    var parts = new List<Rectangle>(4);
    if (intersection.X > target.X)
      parts.Add(Rectangle.FromLTRB(
        target.X, target.Y, intersection.X, target.Bottom));
    if (intersection.Right < target.Right)
      parts.Add(Rectangle.FromLTRB(
        intersection.Right, target.Y, target.Right, target.Bottom));
    if (intersection.Y > target.Y)
      parts.Add(Rectangle.FromLTRB(
        intersection.X, target.Y, intersection.Right, intersection.Y));
    if (intersection.Bottom < target.Bottom)
      parts.Add(Rectangle.FromLTRB(
        intersection.X, intersection.Bottom, intersection.Right,
        target.Bottom));
    return parts;
  }
}
