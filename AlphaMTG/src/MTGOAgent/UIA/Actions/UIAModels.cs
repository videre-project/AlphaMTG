/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Drawing;
using AlphaMTG.MTGOAgent.UIA;

namespace AlphaMTG.MTGOAgent.UIA.Actions;

/// <summary>
/// Identifies one target within one target set of an action.
/// </summary>
/// <param name="TargetSetId">The <c>TargetSetState.Index</c> to fill.</param>
/// <param name="TargetId">The <c>GameObjectState.Id</c> to click.</param>
internal sealed record TargetSelection(int TargetSetId, int TargetId);

/// <summary>
/// Assigns one distributed amount to one target of a distributing action.
/// </summary>
/// <param name="TargetId">The <c>GameObjectState.Id</c> of the target.</param>
/// <param name="Value">The amount to assign to that target.</param>
internal sealed record DistributionInput(int TargetId, int Value);

/// <summary>
/// Describes what a single executor step actually clicked.
/// </summary>
/// <param name="ActionId">The state-file action that was driven.</param>
/// <param name="Operation">The kind of interaction performed.</param>
/// <param name="Name">The visible label that was clicked.</param>
/// <param name="Value">The object id the label resolved to, if any.</param>
/// <param name="Detail">A human-readable note about the step.</param>
internal sealed record ActionExecution(
  int ActionId,
  string Operation,
  string Name,
  int? Value,
  string Detail);

/// <summary>
/// Selects a card or a player during MTGO's current selection interaction.
/// </summary>
/// <remarks>
/// Selection is only meaningful while MTGO is asking for a target. The caller
/// should confirm that from the exported state before issuing this; the
/// executor verifies the target exists in the tree but deliberately does not
/// decide whether it is a legal choice.
/// </remarks>
/// <param name="CardId">The card to click, or null to select a player.</param>
/// <param name="PlayerName">
/// The player's name, used only when <paramref name="CardId"/> is null.
/// </param>
internal sealed record SelectionTarget(int? CardId, string? PlayerName);

/// <summary>
/// Read-only geometry for a battlefield card, including the area left after
/// the other live battlefield card views have been subtracted.
/// </summary>
internal sealed record CardClickPreview(
  int CardId,
  Rectangle Target,
  IReadOnlyList<Rectangle> Occluders,
  IReadOnlyList<Rectangle> VisibleAreas,
  Point? ClickPoint);
