/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

using MTGOSDK.API.Play.Games.Processors;

namespace AlphaMTG.MTGOAgent.UIA;

/// <summary>
/// Performs actions by driving MTGO's UI the way a person would.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately contains no game logic. It never inspects a target
/// set for legality, never advances a multi-target sequence, and never decides
/// that an action is complete. It locates the control a person would click and
/// clicks it; MTGO's own state machine owns every rule and transition.
/// </para>
/// <para>
/// The one thing it does assert is freshness: the caller must pass the nonce
/// it read, so a click is never delivered against a state the client has
/// already moved past.
/// </para>
/// </remarks>
internal sealed class ActionExecutor(
  string statePath,
  CardActionRegistry? registry = null) : IDisposable
{
  private readonly UiaSession _session = new();
  private readonly CardActionRegistry? _registry = registry;
  private AgentState _state = null!;

  public void Dispose() => _session.Dispose();

  public ActionExecution Execute(
    int gameId,
    int nonce,
    int actionId,
    TargetSelection? target,
    int? choiceId)
  {
    AgentState state = AgentStateStore.Read(statePath);
    if (state.GameId != gameId)
    {
      throw new ArgumentException(
        $"The state file holds game {state.GameId}, not {gameId}.");
    }
    if (state.Nonce != nonce)
    {
      throw new ArgumentException(
        $"The state file is at nonce {state.Nonce}, not the requested "
          + $"{nonce}. Re-read the state before acting.");
    }

    AgentAction action = state.Actions.SingleOrDefault(candidate =>
      candidate.Id == actionId)
      ?? throw new ArgumentException(
        $"No action with id {actionId} is present in the state file.");

    if (target is not null)
    {
      RequireTargets(action, target);
      return ClickTargets(state, action, target);
    }

    if (choiceId is not null)
    {
      RequireChoices(action, choiceId.Value);
      return ClickChoice(state, action, choiceId.Value);
    }

    return action.Source.Type switch
    {
      "prompt" => ClickPromptAction(action),
      "card" => ClickCardAction(state, action),
      _ => throw new InvalidOperationException(
        $"Action {actionId} has unsupported source "
          + $"'{action.Source.Type}'.")
    };
  }

  /// <summary>
  /// Activates a card menu entry, then clicks the requested target if the
  /// action opened MTGO's targeting interaction.
  /// </summary>
  private ActionExecution ClickCardAction(AgentState state, AgentAction action)
  {
    int cardId = action.Source.CardId
      ?? throw new InvalidOperationException(
        $"Card action '{action.Name}' has no source card.");

    CardState card = state.Cards.Single(candidate => candidate.Id == cardId);
    AutomationElement? element = _session.FindCardElement(
      card.Zone,
      card.Name,
      card.NameOrdinal ?? 0)
      ?? throw new InvalidOperationException(
        $"No visible {card.Zone} element for {card.Name} "
          + $"(card {card.Id}).");

    DismissStaleMenu();
    element.RightClick(moveMouse: true);

    AutomationElement? menu = _session.WaitForContextMenu(
      TimeSpan.FromSeconds(2))
      ?? throw new InvalidOperationException(
        "MTGO did not open a card menu after the right click.");

    try
    {
      AutomationElement item = FindMenuItem(menu, action.Name);
      item.Click(moveMouse: true);
    }
    finally
    {
      DismissStaleMenu();
    }

    return new ActionExecution(
      action.Id,
      "card-action",
      action.Name,
      cardId,
      "Clicked the card menu entry.");
  }

  /// <summary>
  /// Clicks a prompt button by its visible label.
  /// </summary>
  private ActionExecution ClickPromptAction(AgentAction action) =>
    ClickControl(action.Name, action.Id, "prompt-action");

  /// <summary>
  /// Clicks each requested target, then the prompt's own confirm control.
  /// </summary>
  private ActionExecution ClickTargets(
    AgentState state,
    AgentAction action,
    TargetSelection target)
  {
    TargetSetState set = action.Targets!.Single(candidate =>
      candidate.Index == target.TargetSetId);

    GameObjectState wanted = set.Legal.Single(candidate =>
      candidate.Id == target.TargetId);

    if (wanted.Type == "card")
    {
      CardState card = state.Cards.Single(candidate =>
        candidate.Id == wanted.Id);
      AutomationElement? element = _session.FindCardElement(
        card.Zone,
        card.Name,
        card.NameOrdinal ?? 0)
        ?? throw new InvalidOperationException(
          $"No visible element for target {card.Name} (card {card.Id}).");
      element.Click(moveMouse: true);
    }
    else
    {
      AutomationElement? player = _session.FindControlByLabel(wanted.Name)
        ?? throw new InvalidOperationException(
          $"No visible control for target {wanted.Name}.");
      player.Click(moveMouse: true);
    }

    // MTGO decides whether more targets or a confirmation are required; we
    // only click the confirm control if the client is actually showing one.
    ActionExecution result = new(
      action.Id,
      "target",
      wanted.Name,
      wanted.Id,
      "Clicked the target; MTGO owns the remaining sequence.");
    ConfirmIfPrompted();
    return result;
  }

  /// <summary>
  /// Clicks a choice entry exposed by the action.
  /// </summary>
  private ActionExecution ClickChoice(
    AgentState state,
    AgentAction action,
    int choiceId)
  {
    ActionChoice choice = action.Choices!.Single(candidate =>
      candidate.Id == choiceId);
    AutomationElement? element = _session.FindControlByLabel(choice.Name)
      ?? throw new InvalidOperationException(
        $"No visible control for choice '{choice.Name}'.");

    element.Click(moveMouse: true);
    return new ActionExecution(
      action.Id,
      "choice",
      choice.Name,
      choiceId,
      "Clicked the choice.");
  }

  /// <summary>
  /// Clicks a card or player that MTGO has highlighted as selectable.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Cards are resolved natively: the registry reports whether the client will
  /// accept a click, and its own view model reports where the card is drawn
  /// right now. That avoids matching cards by name, which cannot tell two
  /// same-named permanents apart. The position is read at click time rather
  /// than taken from the state file, because a coordinate captured earlier can
  /// be stale by the time it is used.
  /// </para>
  /// <para>
  /// Players have no native position - a player view model holds no reference
  /// to its own view - so they resolve through the automation tree by name
  /// inside the duel scene. The executor fails rather than clicking blindly,
  /// because a mis-click during a turn is worse than an error.
  /// </para>
  /// </remarks>
  public ActionExecution Select(SelectionTarget target)
  {
    _state = AgentStateStore.Read(statePath);

    if (target.CardId is int cardId)
      return SelectCard(cardId);

    if (!string.IsNullOrEmpty(target.PlayerName))
      return SelectPlayer(target.PlayerName);

    throw new ArgumentException(
      "A selection needs either a card id or a player name.");
  }

  private ActionExecution SelectCard(int cardId)
  {
    CardActionRegistry? registry = _registry;
    if (registry is null)
    {
      throw new InvalidOperationException(
        "No live MTGO game was attached, so card selection cannot resolve a "
          + "card's position natively.");
    }

    CardSelectable selectable = registry.GetSelectable(cardId);
    if (!selectable.IsSelectable)
    {
      throw new InvalidOperationException(
        $"Card {cardId} is not selectable right now.");
    }

    DismissStaleMenu();

    ScreenPoint position = ResolveClickPosition(registry, cardId)
      ?? throw new InvalidOperationException(
        $"Card {cardId} is selectable but is not on screen, so it cannot "
          + "be clicked.");

    // Click the center, not the origin: the origin is the view's top-left
    // corner, which can land on a frame edge, a highlight border, or a
    // neighboring element, all of which MTGO ignores. On cluttered
    // battlefields identical creatures stack, so the point is first narrowed
    // to the card's visible remainder (all native rects, no tree walk).
    CardState? card = _state.Cards
      .FirstOrDefault(candidate => candidate.Id == cardId);
    Point click = card?.Zone is "Battlefield"
      ? ComputeBattlefieldClickArea(registry, card, position).ClickPoint
          ?? throw new InvalidOperationException(
            $"Card {cardId} has no safe uncovered area to click.")
      : new Point(
        position.X + position.Width / 2, position.Y + position.Height / 2);
    UiaSession.ClickAt(click.X, click.Y);

    // Verification reads the flag back rather than walking the tree again:
    // a landed target selection sets IsTargeted on the card. Recorded, not
    // thrown — outside targeting mode the flag never sets.
    bool confirmed = ConfirmTargeted(registry, cardId);
    return new ActionExecution(
      0,
      "select-card",
      card?.Name ?? $"card {cardId}",
      cardId,
      $"Clicked the card at {click.X},{click.Y}. Target selection "
        + $"{(confirmed ? "confirmed" : "not observed within 3s")}.");
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
    CardActionRegistry registry = _registry
      ?? throw new InvalidOperationException("No live MTGO game was attached.");
    _state ??= AgentStateStore.Read(statePath);
    CardState card = _state.Cards.Single(candidate => candidate.Id == cardId);
    if (card.Zone != "Battlefield")
      throw new InvalidOperationException(
        $"Card {cardId} is in {card.Zone}, not the battlefield.");
    ScreenPoint position = registry.GetPosition(cardId)
      ?? throw new InvalidOperationException(
        $"Card {cardId} has no live battlefield position.");
    return ComputeBattlefieldClickArea(registry, card, position);
  }

  private CardClickPreview ComputeBattlefieldClickArea(
    CardActionRegistry registry,
    CardState card,
    ScreenPoint position)
  {
    var target = new Rectangle(position.X, position.Y, position.Width, position.Height);
    var ids = _state.Cards
      .Where(candidate => candidate.Zone is "Battlefield" && candidate.Id != card.Id)
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
    _state ??= AgentStateStore.Read(statePath);

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
    UiaSession.ZoneListPanel? panel = FindOwnerZonePanel(card);
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
    UiaSession.ZoneListPanel? panel = FindOwnerZonePanel(card);
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
      UiaSession.ZoneListPanel? panel = FindOwnerZonePanel(card);
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

  private UiaSession.ZoneListPanel? FindOwnerZonePanel(CardState card)
  {
    string? owner = _state.Players
      .FirstOrDefault(player => player.Index == card.OwnerIndex)
      ?.Name;
    if (string.IsNullOrEmpty(owner)) return null;

    return _session.FindZoneListPanel(owner);
  }

  /// <summary>
  /// Clicks a card shown in a pop-out dialog (trigger ordering, card
  /// selection lists) at a point verified visible, i.e. not covered by any
  /// other card.
  /// </summary>
  /// <remarks>
  /// Dialog items are neither actions nor target selections: they carry no
  /// <see cref="CardAction"/> and are not selectable, so <see cref="Select"/>
  /// refuses them. Dialog art is distinguished from battlefield art by its
  /// UIA container. Overlap subtraction is scoped to images in that same
  /// container. Verification reports whether the image remains visible after
  /// the click; MTGO may keep a selected item visible.
  /// </remarks>
  public ActionExecution SelectDialogItem(string cardName, int? index = null)
  {
    DialogClickPreview preview = PreviewDialogItem(cardName, index);
    Point point = preview.ClickPoint;
    UiaSession.ClickAt(point.X, point.Y);

    Thread.Sleep(1000);
    string wantedId = "Image-" + cardName;
    bool cleared = !_session.FindImages()
      .Any(image => string.Equals(
        image.AutomationId, wantedId, StringComparison.Ordinal)
        && image.ParentClassName == "DuelView"
        && OverlapFraction(image.Bounds, preview.Target) >= 0.5);

    return new ActionExecution(
      0,
      "select-dialog",
      cardName,
      null,
      $"Clicked {cardName} at {point.X},{point.Y} (target "
        + $"{preview.Target.X},{preview.Target.Y} "
        + $"{preview.Target.Width}x{preview.Target.Height}, "
        + $"{preview.VisibleAreas.Count} visible regions, "
        + $"{preview.Occluders.Count} occluders; "
        + $"{preview.CandidateCount} candidates). Dialog image "
        + $"{(cleared ? "cleared" : "remained visible")} afterwards.");
  }

  /// <summary>
  /// Resolves a dialog card's uncovered art area without moving the mouse or
  /// clicking. The same result is used by SelectDialogItem at click time.
  /// </summary>
  public DialogClickPreview PreviewDialogItem(
    string cardName, int? index = null)
  {
    string wantedId = "Image-" + cardName;
    var images = _session.FindImages();
    var candidates = images
      .Where(image => string.Equals(image.AutomationId, wantedId,
        StringComparison.Ordinal)
        && image.ParentClassName == "DuelView")
      .ToList();

    if ((!index.HasValue && candidates.Count != 1)
        || (index.HasValue && (index.Value < 0 || index.Value >= candidates.Count)))
    {
      throw new InvalidOperationException(
        $"Expected exactly one dialog image for '{cardName}', found "
          + $"{candidates.Count}. Pass --index 0..{candidates.Count - 1} "
          + "to pick one explicitly.");
    }

    UiaSession.RawImage selected = index.HasValue
      ? candidates[index.Value] : candidates[0];
    Rectangle target = selected.Bounds;
    if (target.Width <= 0 || target.Height <= 0)
      throw new InvalidOperationException("Dialog image has no visible area.");

    // UIA bounds cover card art, while neighboring card frames extend beyond
    // their art. Only images in the same dialog container can occlude it.
    const int frameMargin = 12;
    var occluders = images
      .Where(image => !ReferenceEquals(image, selected)
        && image.ParentClassName == selected.ParentClassName)
      .Select(image => image.Bounds)
      .Where(rect => rect.Width > 0 && rect.Height > 0)
      .Select(rect => Rectangle.FromLTRB(
        rect.X - frameMargin, rect.Y - frameMargin,
        rect.Right + frameMargin, rect.Bottom + frameMargin))
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

    // A narrow sliver may be mathematically clear but cannot absorb UIA
    // rounding or a small mouse movement. Require an 8px square minimum.
    var safe = visible
      .Where(region => region.Width >= 8 && region.Height >= 8)
      .OrderByDescending(region => (long)region.Width * region.Height)
      .ToList();
    if (safe.Count == 0)
      throw new InvalidOperationException(
        $"Dialog image for '{cardName}' has no safe uncovered area.");

    Rectangle best = safe[0];
    var point = new Point(best.X + best.Width / 2,
      best.Y + best.Height / 2);
    return new DialogClickPreview(
      cardName, target, occluders, safe, point,
      candidates.Count);
  }

  /// <summary>
  /// Fraction of <paramref name="area"/>'s pixels covered by
  /// <paramref name="cover"/>.
  /// </summary>
  private static double OverlapFraction(Rectangle area, Rectangle cover)
  {
    if (area.Width <= 0 || area.Height <= 0) return 0;
    Rectangle intersection = Rectangle.Intersect(area, cover);
    if (intersection.IsEmpty) return 0;
    return (double)((long)intersection.Width * intersection.Height)
      / ((long)area.Width * area.Height);
  }

  /// <summary>
  /// Splits <paramref name="target"/> around <paramref name="cut"/>,
  /// returning the surviving rectangles.
  /// </summary>
  private static IReadOnlyList<Rectangle> Subtract(
    Rectangle target,
    Rectangle cut)
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

  private ActionExecution SelectPlayer(string playerName)
  {
    var element = _session.FindPlayerBox(playerName)
      ?? throw new InvalidOperationException(
        $"No player box for '{playerName}' in the duel scene.");

    DismissStaleMenu();

    if (!UiaSession.GetIsEnabled(element))
    {
      throw new InvalidOperationException(
        $"'{playerName}' is present but not enabled; it is not selectable now.");
    }

    element.Click(moveMouse: true);

    return new ActionExecution(
      0,
      "select-player",
      playerName,
      null,
      "Clicked the selected object.");
  }

  /// <summary>
  /// Clicks an enabled prompt confirm control when MTGO is showing one.
  /// </summary>
  public ActionExecution ClickControl(
    string label,
    int actionId,
    string operation)
  {
    AutomationElement? element = _session.FindControlByLabel(label)
      ?? throw new InvalidOperationException(
        $"No visible enabled control labelled '{label}'.");

    element.Click(moveMouse: true);
    return new ActionExecution(
      actionId,
      operation,
      label,
      null,
      "Clicked the control.");
  }

  private void ConfirmIfPrompted()
  {
    Thread.Sleep(250);
    AutomationElement? confirm = _session.FindControlByLabel("OK");
    confirm?.Click(moveMouse: true);
  }

  private static AutomationElement FindMenuItem(AutomationElement menu, string name)
  {
    // Scope to real menu items: a context menu container also exposes the
    // card it belongs to, and the popup chrome, as descendants.
    AutomationElement[] entries = menu
      .FindAllDescendants()
      .Where(UiaSession.IsVisible)
      .Where(element => UiaSession.GetControlType(element)
        == ControlType.MenuItem)
      .ToArray();

    AutomationElement[] items = entries
      .Where(UiaSession.GetIsEnabled)
      .Where(element => UiaSession.LabelsMatch(UiaSession.GetName(element), name))
      .ToArray();

    if (items.Length == 1) return items[0];

    string available = string.Join(", ", entries.Select(element =>
      $"'{UiaSession.GetName(element) ?? "<unnamed>"}'"
        + (UiaSession.GetIsEnabled(element) ? "" : " (disabled)")));

    return items.Length == 0
      ? throw new InvalidOperationException(
        $"No enabled menu entry labelled '{name}' was open. Entries: "
          + available)
      : throw new InvalidOperationException(
        $"'{name}' matched {items.Length} menu entries. Entries: {available}");
  }

  private void DismissStaleMenu()
  {
    if (!_session.HasContextMenu()) return;

    _session.DismissContextMenu();
    if (_session.HasContextMenu())
    {
      throw new InvalidOperationException(
        "A card menu was already open and could not be dismissed.");
    }
  }

  private static void RequireTargets(AgentAction action, TargetSelection target)
  {
    if (action.Targets is null || action.Targets.Count == 0)
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') declares no targets.");
    }
  }

  private static void RequireChoices(AgentAction action, int choiceId)
  {
    if (action.Choices is null || action.Choices.Count == 0)
    {
      throw new ArgumentException(
        $"Action {action.Id} ('{action.Name}') declares no choices.");
    }
  }
}
