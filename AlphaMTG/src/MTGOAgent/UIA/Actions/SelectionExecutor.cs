/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.Core.Reflection;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Interop;
using AlphaMTG.MTGOAgent.UIA;

namespace AlphaMTG.MTGOAgent.UIA.Actions;

/// <summary>
/// The selection world: clicks on cards, players, and dialog items that
/// MTGO has highlighted as selectable, plus the geometry those clicks need.
/// </summary>
/// <remarks>
/// This type deliberately contains no game logic. It never inspects a target
/// set for legality, never advances a multi-target sequence, and never
/// decides that an action is complete. It locates the visual a person would
/// click and clicks it; MTGO's own state machine owns every rule and
/// transition. Game actions live in <see cref="ActionExecutor"/>.
/// </remarks>
internal sealed class SelectionExecutor(
  Game game,
  AgentState state,
  CardActionRegistry registry,
  UIASession session)
{
  private PlayerBoxes Players => new(session);
  private ZonePanels Zones => new(session);
  private readonly Game _game = game;
  private readonly AgentState _state = state;
  private readonly CardActionRegistry _registry = registry;

  /// <summary>
  /// Clicks a card or player that MTGO has highlighted as selectable.
  /// </summary>
  public ActionExecution Select(SelectionTarget target)
  {

    if (target.CardId is int cardId)
      return SelectCard(cardId);

    if (!string.IsNullOrEmpty(target.PlayerName))
      return SelectPlayer(target.PlayerName);

    throw new ArgumentException(
      "A selection needs either a card id or a player name.");
  }

  internal ActionExecution SelectCard(int cardId)
  {
    CardActionRegistry registry = _registry;

    CardSelectable selectable = registry.GetSelectable(cardId);
    if (!selectable.IsSelectable)
    {
      throw new InvalidOperationException(
        $"Card {cardId} is not selectable right now.");
    }

    int clickCardId = cardId;
    ScreenPoint resolved = ResolveClickPosition(registry, clickCardId)
      ?? throw new InvalidOperationException(
        $"Card {clickCardId} is selectable but is not on screen, so it cannot "
          + "be selected.");

    // Narrow battlefield points to the card's visible remainder (all native
    // rects, no tree walk): the origin can land on a frame edge, a highlight
    // border, or a neighboring element, all of which MTGO ignores.
    CardState? resolvedCard = _state.Cards
      .FirstOrDefault(candidate => candidate.Id == clickCardId);
    CardState zoneCard = resolvedCard
      ?? throw new InvalidOperationException(
        $"Card {clickCardId} is not in the state file.");
    System.Drawing.Point click =
      ComputeSafeClickArea(registry, zoneCard, resolved).ClickPoint
        ?? throw new InvalidOperationException(
          $"Card {clickCardId} has no safe uncovered area to click.");

    SelectionResult selection = SelectionClick.Select(
      click.X,
      click.Y,
      clickCardId,
      _game);
    if (!selection.Resolved)
    {
      throw new InvalidOperationException(
        $"No selectable visual under ({click.X},{click.Y}) for card "
        + $"{clickCardId}: {selection.Detail}");
    }

    // Verification reads the flag back rather than walking the tree again:
    // a landed target selection sets IsTargeted on the card. Recorded, not
    // thrown — outside targeting mode the flag never sets.
    bool confirmed = ConfirmTargeted(registry, cardId);
    return new ActionExecution(
      0,
      "select-card",
      resolvedCard?.Name ?? $"card {cardId}",
      cardId,
      $"Selected the card at {click.X},{click.Y}. Target selection "
        + $"{(confirmed ? "confirmed" : "not observed within 3s")}.");
  }

  /// <summary>
  /// Selects a card by id: native safe point plus synthesized selection.
  /// </summary>
  internal void SelectCardNative(int cardId)
  {
    CardActionRegistry registry = _registry;

    ScreenPoint? position = ResolveClickPosition(registry, cardId);
    CardState? card = _state.Cards
      .FirstOrDefault(candidate => candidate.Id == cardId);
    System.Drawing.Point point;
    if (position is null)
    {
      throw new InvalidOperationException(
        $"Card {cardId} is selectable but is not on screen, so it cannot "
          + "be selected.");
    }
    else
    {
      CardState zoneCard = card
        ?? throw new InvalidOperationException(
          $"Card {cardId} is not in the state file.");
      point = ComputeSafeClickArea(registry, zoneCard, position).ClickPoint
        ?? throw new InvalidOperationException(
          $"Card {cardId} has no safe uncovered area to click.");
    }

    SelectionResult selection = SelectionClick.Select(
      point.X,
      point.Y,
      cardId,
      _game);
    if (!selection.Resolved)
    {
      throw new InvalidOperationException(
        $"No selectable visual under ({point.X},{point.Y}) for card {cardId}: "
        + selection.Detail);
    }
  }

  /// <summary>
  /// Selects a player by name through the player box plus synthesis.
  /// Players expose no native position, so the box resolves by name.
  /// </summary>
  internal void SelectPlayerNative(string playerName)
  {
    AutomationElement? element = Players.FindPlayerBox(playerName)
      ?? throw new InvalidOperationException(
        $"No player box for '{playerName}' in the duel scene.");

    if (!UIASession.GetIsEnabled(element))
    {
      throw new InvalidOperationException(
        $"'{playerName}' is present but not enabled; it is not selectable now.");
    }

    var bounds = UIASession.GetBounds(element)
      ?? throw new InvalidOperationException(
        $"Player box for '{playerName}' has no bounds.");
    Rectangle box = bounds;
    int anchorCardId = _state.Cards
      .Where(candidate => candidate.Zone is "Battlefield" or "Hand")
      .Select(candidate => candidate.Id)
      .FirstOrDefault();
    if (anchorCardId == 0)
    {
      throw new InvalidOperationException(
        "No live card available to anchor player selection.");
    }

    SelectionResult selection = SelectionClick.Select(
      box.X + box.Width / 2,
      box.Y + box.Height / 2,
      anchorCardId,
      _game);
    if (!selection.Resolved)
    {
      throw new InvalidOperationException(
        $"No selectable visual for player '{playerName}': "
        + selection.Detail);
    }
  }

  private ActionExecution SelectPlayer(string playerName)
  {
    SelectPlayerNative(playerName);

    return new ActionExecution(
      0,
      "select-player",
      playerName,
      null,
      "Selected the player.");
  }

  /// <summary>
  /// The center of the card's largest visible remainder after subtracting
  /// every other battlefield rect.
  /// </summary>
  /// <remarks>
  /// Z-order is unknown, so every other rect is treated as on top; the
  /// remainder is clickable regardless of actual stacking.
  /// </remarks>
  public CardClickPreview PreviewCard(int cardId)
  {
    CardActionRegistry registry = _registry;
    CardState card = _state.Cards.Single(candidate => candidate.Id == cardId);
    if (card.Zone != "Battlefield")
      throw new InvalidOperationException(
        $"Card {cardId} is in {card.Zone}, not the battlefield.");
    ScreenPoint position = registry.GetPosition(cardId)
      ?? throw new InvalidOperationException(
        $"Card {cardId} has no live battlefield position.");
    return ComputeSafeClickArea(registry, card, position);
  }

  /// <summary>
  /// The center of the card's largest visible remainder after subtracting
  /// every other rect in the same zone.
  /// </summary>
  /// <remarks>
  /// Z-order is unknown, so every other rect is treated as on top; the
  /// remainder is clickable regardless of actual stacking. Applies to every
  /// zone: battlefield stacks, fanned hands, and graveyard/exile rows all
  /// overlap, and a naive center can land on a neighboring card.
  /// </remarks>
  private CardClickPreview ComputeSafeClickArea(
    CardActionRegistry registry,
    CardState card,
    ScreenPoint position)
  {
    var target = new Rectangle(position.X, position.Y, position.Width, position.Height);
    var ids = _state.Cards
      .Where(candidate => candidate.Zone == card.Zone && candidate.Id != card.Id)
      .Select(candidate => candidate.Id)
      .ToList();
    var occluders = registry.GetPositionsBatch(ids).Values
      .Select(point => new Rectangle(point.X, point.Y, point.Width, point.Height))
      .Where(rect => rect.Width > 0 && rect.Height > 0)
      .Where(rect => rect.IntersectsWith(target))
      .Distinct()
      .ToList();

    var visible = new List<Rectangle> { target };
    foreach (Rectangle cut in occluders)
    {
      var next = new List<Rectangle>();
      foreach (Rectangle region in visible)
        next.AddRange(Subtract(region, cut));
      visible = next;
      if (visible.Count == 0) break;
    }

    // Ignore tiny rounding artifacts, while preserving the narrow exposed
    // strips that stacked battlefield cards can leave.
    var safe = visible
      .Where(region => region.Width >= 8 && region.Height >= 8)
      .OrderByDescending(region => (long)region.Width * region.Height)
      .ToList();
    Point? point = safe.Count == 0
      ? null
      : new Point(safe[0].X + safe[0].Width / 2,
        safe[0].Y + safe[0].Height / 2);
    return new CardClickPreview(card.Id, target, occluders, visible, point);
  }

  /// <summary>
  /// Polls the card's targeted flag briefly after a click.
  /// </summary>
  private bool ConfirmTargeted(CardActionRegistry registry, int cardId)
  {
    for (int attempt = 0; attempt < 6; attempt++)
    {
      try
      {
        if (registry.GetSelectable(cardId).IsTargeted)
          return true;
      }
      catch
      {
        return false;
      }

      Thread.Sleep(500);
    }

    return false;
  }

  /// <summary>
  /// Resolves a verified clickable position for a card, scrolling its zone
  /// list when the card is selectable but off screen. No click is performed.
  /// </summary>
  public ScreenPoint? ResolveClickPosition(CardActionRegistry registry, int cardId)
  {

    ScreenPoint? position = registry.GetPosition(cardId);

    CardState? card = _state.Cards
      .FirstOrDefault(candidate => candidate.Id == cardId);

    if (position is not null && card?.Zone is "Graveyard" or "Exile")
    {
      position = EnsureInZoneViewport(registry, card, position);
    }

    return position;
  }

  /// <summary>
  /// Returns the position when it sits comfortably inside the owner's zone
  /// list viewport, scrolls there when it does not, and fails when no panel
  /// is found or scrolling does not converge.
  /// </summary>
  /// <remarks>
  /// When no panel is found (battlefield, hand, stack, pop-out zones) the
  /// position is returned as-is: there is no scroller to consult, so this
  /// preserves today's behavior for those zones.
  /// </remarks>
  private ScreenPoint? EnsureInZoneViewport(
    CardActionRegistry registry,
    CardState card,
    ScreenPoint position)
  {
    ZoneListPanel? panel = FindOwnerZonePanel(card);
    if (panel is null) return position;

    // Aim for the middle of the viewport, not merely inside it, so the
    // settled position is comfortably clickable rather than a sliver.
    int comfort = panel.Viewport.Height / 3;
    if (IsInZoneViewport(card, position, comfort)) return position;

    return ScrollCardIntoView(registry, card, position, comfort);
  }

  /// <summary>
  /// Reports whether a resolved position lies inside the visible viewport of
  /// the owner's zone list panel.
  /// </summary>
  /// <param name="yMargin">
  /// How far inside the viewport's top and bottom edges the point must be. A
  /// bare containment check passes slivers of rows hanging over the edge,
  /// which are not clickable. The horizontal check uses a small fixed margin:
  /// these panels scroll vertically, so comfort only ever applies to Y.
  /// </param>
  private bool IsInZoneViewport(CardState card, ScreenPoint position, int yMargin = 8)
  {
    ZoneListPanel? panel = FindOwnerZonePanel(card);
    return panel is not null
      && position.X >= panel.Viewport.X + 8
      && position.X < panel.Viewport.X + panel.Viewport.Width - 8
      && position.Y >= panel.Viewport.Y + yMargin
      && position.Y < panel.Viewport.Y + panel.Viewport.Height - yMargin;
  }

  /// <summary>
  /// Scrolls a zone list until a card's freshly resolved position lands inside
  /// the viewport, or fails loudly when it does not.
  /// </summary>
  /// <remarks>
  /// Each step invokes the panel's own repeat button and re-reads the native
  /// position, so the loop stops on the verified coordinate rather than on a
  /// predicted scroll distance. Bounded: a panel that does not move fails
  /// instead of scrolling forever. Pattern Invoke is used rather than the
  /// mouse so scrolling does not depend on pointer delivery.
  /// </remarks>
  private ScreenPoint? ScrollCardIntoView(
    CardActionRegistry registry,
    CardState card,
    ScreenPoint position,
    int comfort)
  {
    var start = position;
    int attempts = 0;

    for (attempts = 0; attempts < 10; attempts++)
    {
      ZoneListPanel? panel = FindOwnerZonePanel(card);
      if (panel is null) return null;

      ScreenPoint? fresh = registry.GetPosition(card.Id);
      if (fresh is null) return null;
      position = fresh;

      if (IsInZoneViewport(card, position, comfort)) return position;

      bool below = position.Y >= panel.Viewport.Y + panel.Viewport.Height - comfort;
      AutomationElement button = below ? panel.DownButton : panel.UpButton;
      try
      {
        button.AsButton().Invoke();
      }
      catch (Exception exception)
      {
        throw new InvalidOperationException(
          $"Card {card.Id} scroll button did not invoke "
          + $"({exception.GetType().Name}).",
          exception);
      }

      Thread.Sleep(400);
    }

    throw new InvalidOperationException(
      $"Card {card.Id} did not reach the viewport in {attempts} scrolls "
        + $"(start {start.X},{start.Y}, end {position.X},{position.Y}).");
  }

  /// <summary>
  /// Selects a card in the trigger pop-out by name, resolving frames
  /// natively and subtracting sibling frames for a safe point.
  /// </summary>
  public ActionExecution SelectDialogCard(string cardName)
  {
    int anchorCardId = _state.Cards
      .Where(candidate => candidate.Zone is "Battlefield" or "Hand")
      .Select(candidate => candidate.Id)
      .FirstOrDefault();
    if (anchorCardId == 0)
    {
      throw new InvalidOperationException(
        "No live card available to resolve the duel scene.");
    }

    (Rectangle Frame, Point? SafePoint)? match = null;
    string? matchName = null;
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic? scene = ResolveScene(anchorCardId);
      if (scene is null)
      {
        throw new InvalidOperationException(
          "No duel scene resolved for dialog selection.");
      }

      var frames = new List<(string Name, Rectangle Frame)>();
      foreach (dynamic card in
        (System.Collections.IEnumerable)scene.LocalTriggerZone.Cards)
      {
        string? name = null;
        try { name = card.CardDefinition.Name; }
        catch { }
        if (name is null) continue;
        try
        {
          dynamic view = card.m_battlefieldPositionView;
          if (view is null) continue;
          dynamic origin = RemoteClient.CreateInstance<
            System.Windows.Point>(0d, 0d);
          dynamic point = view.PointToScreen(origin);
          frames.Add((name,
            new Rectangle(
              Convert.ToInt32(Convert.ToDouble(point.X)),
              Convert.ToInt32(Convert.ToDouble(point.Y)),
              Convert.ToInt32(Convert.ToDouble(view.ActualWidth)),
              Convert.ToInt32(Convert.ToDouble(view.ActualHeight)))));
        }
        catch
        {
          // Cards without a live view cannot be clicked; skip them.
        }
      }

      foreach ((string Name, Rectangle Frame) candidate in frames
        .Where(entry => string.Equals(
          entry.Name, cardName, StringComparison.OrdinalIgnoreCase)))
      {
        var visible = new List<Rectangle> { candidate.Frame };
        foreach ((string _, Rectangle occluder) in frames)
        {
          if (occluder == candidate.Frame) continue;
          var next = new List<Rectangle>();
          foreach (Rectangle region in visible)
            next.AddRange(Subtract(region, occluder));
          visible = next;
          if (visible.Count == 0) break;
        }
        var safe = visible
          .Where(region => region.Width >= 8 && region.Height >= 8)
          .OrderByDescending(region => (long)region.Width * region.Height)
          .ToList();
        if (safe.Count == 0) continue;
        Rectangle best = safe[0];
        match = (candidate.Frame,
          new Point(best.X + best.Width / 2, best.Y + best.Height / 2));
        matchName = candidate.Name;
        break;
      }
    }

    if (match is null)
    {
      throw new InvalidOperationException(
        $"No uncovered dialog card named '{cardName}'.");
    }

    SelectionResult selection = SelectionClick.Select(
      match.Value.SafePoint!.Value.X,
      match.Value.SafePoint!.Value.Y,
      anchorCardId,
      _game);
    if (!selection.Resolved)
    {
      throw new InvalidOperationException(
        $"No selectable visual for dialog card '{matchName}': "
        + selection.Detail);
    }

    return new ActionExecution(
      0,
      "select-dialog",
      matchName ?? cardName,
      null,
      $"Selected dialog card at {match.Value.SafePoint!.Value.X},"
        + $"{match.Value.SafePoint!.Value.Y}.");
  }

  internal dynamic? ResolveScene(int anchorCardId)
  {
    try
    {
      MTGOSDK.API.Play.Games.GameCard? sdkCard =
        _game.GetGameCard(anchorCardId);
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

  private ZoneListPanel? FindOwnerZonePanel(CardState card)
  {
    string? owner = _state.Players
      .FirstOrDefault(player => player.Index == card.OwnerIndex)
      ?.Name;
    if (string.IsNullOrEmpty(owner)) return null;

    return Zones.FindZoneListPanel(owner);
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
