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
using ScreenBounds = MTGOSDK.API.Interface.ViewModels.CardViewModel.ScreenBounds;


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

    // Scroll zone lists before capturing the target and neighboring views together.
    if (zone is "Graveyard" or "Exile")
    {
      ScreenBounds initial = scene.GetCardPosition(card.Id)
        ?? throw new InvalidOperationException($"Card {card.Id} has no live screen position.");
      EnsureInZoneViewport(card, initial, scene);
    }

    var ids = _update.Snapshot.Cards.Values.Concat(_update.Snapshot.HiddenCards.Values)
      .Where(candidate => candidate.Zone?.Name == zone)
      .Select(candidate => candidate.Id)
      .ToList();
    var positions = scene.GetCardPositions(ids);
    CardPosition placement = positions.GetValueOrDefault(card.Id)
      ?? throw new InvalidOperationException($"Card {card.Id} has no live screen position.");
    ScreenBounds position = placement.Bounds;
    var target = new Rectangle(position.X, position.Y, position.Width, position.Height);
    var occluders = positions.Where(pair => pair.Key != card.Id)
      .Select(pair => pair.Value)
      .Where(other => other.PanelIndex != placement.PanelIndex
        || other.ZIndex > placement.ZIndex
        || (other.ZIndex == placement.ZIndex && other.ChildIndex > placement.ChildIndex))
      .Select(other => other.Bounds)
      .Select(point => new Rectangle(point.X, point.Y, point.Width, point.Height))
      .Where(rect => rect.Width > 0 && rect.Height > 0)
      .Where(rect => rect.IntersectsWith(target))
      .Distinct()
      .ToList();

    List<Rectangle> visible = UncoveredRegions(target, occluders);
    Point? point = SafePoint(visible);
    return (target, occluders, visible, point, positions);
  }

  /// <summary>
  /// Brings a card into the owner's scrollable zone viewport, checking native
  /// coordinates after each scroll. The panel is resolved once for this selection.
  /// </summary>
  private void EnsureInZoneViewport(
    GameCard card, ScreenBounds position, DuelSceneViewModel scene)
  {
    var panel = FindOwnerZonePanel(card, scene);
    if (panel is null) return;

    Rectangle viewport = panel.Value.Viewport;
    int comfort = viewport.Height / 3;
    var start = position;
    for (int attempts = 0; attempts < 10; attempts++)
    {
      if (IsInZoneViewport(viewport, position, comfort)) return;

      bool below = position.Y >= viewport.Bottom - comfort;
      AutomationElement button = below ? panel.Value.DownButton : panel.Value.UpButton;
      button.AsButton().Invoke();
      Thread.Sleep(400);
      position = scene.GetCardPosition(card.Id)
        ?? throw new InvalidOperationException($"Card {card.Id} has no live screen position.");
    }
    if (IsInZoneViewport(viewport, position, comfort)) return;

    throw new InvalidOperationException(
      $"Card {card.Id} did not reach the viewport in 10 scrolls "
        + $"(start {start.X},{start.Y}, end {position.X},{position.Y}).");
  }

  private static bool IsInZoneViewport(
    Rectangle viewport,
    ScreenBounds position,
    int yMargin) =>
    position.X >= viewport.X + 8
      && position.X < viewport.Right - 8
      && position.Y >= viewport.Y + yMargin
      && position.Y < viewport.Bottom - yMargin;

  private (Rectangle Viewport, AutomationElement UpButton, AutomationElement DownButton)?
    FindOwnerZonePanel(GameCard card, DuelSceneViewModel scene)
  {
    string? owner = _update.Snapshot.Players
      .GetValueOrDefault(card.OwnerIndex)?.Name;
    if (string.IsNullOrEmpty(owner)) return null;

    return _session.Value.FindZoneListPanel(owner, SelectionClick.GetWindowHandle(scene));
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
