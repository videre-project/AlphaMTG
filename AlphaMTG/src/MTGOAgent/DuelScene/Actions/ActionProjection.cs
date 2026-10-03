/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Games.Processors.EventArgs;
using MTGOSDK.Core.Reflection;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Reflection.Serialization;

namespace AlphaMTG.MTGOAgent;

internal static class ActionProjection
{

  internal static WPFActionSnapshot ProjectWPFAction(
    CardAction action,
    int index)
  {
    dynamic? rawAction = null;
    try
    {
      rawAction = Unbind(action);
    }
    catch
    {
      // The derived projection below will use safe defaults for stale actions.
    }

    return new WPFActionSnapshot(
      index,
      Try<int>(() => action.ActionId),
      Try<string>(() => action.Type.ToString()) ?? "Unknown",
      Try<string>(() => action.Name) ?? string.Empty,
      Try<string?>(() => Convert.ToString(rawAction?.GroupName)),
      Try<string?>(() => Convert.ToString(rawAction?.ActionChoices)),
      Try<string?>(() => Convert.ToString(rawAction?.ModeChoiceMapping)),
      Try<int>(() => action.Targets.Count),
      Try<bool>(() => (bool)(rawAction?.CanBePerformedLocally ?? false)),
      Try<bool>(() => (bool)(rawAction?.AltMenuAction ?? false)),
      Try<int>(() => Convert.ToInt32(rawAction?.AttackVictimId ?? 0)));
  }

  internal static CardState ProjectCard(ResolvedCard card)
  {
    return new CardState(
      card.Id,
      card.Name ?? string.Empty,
      card.Zone,
      card.OwnerIndex
    );
  }

  internal static AgentAction? TryProjectAction(
    int id,
    ActionCandidate candidate)
  {
    try
    {
      return ProjectAction(id, candidate);
    }
    catch
    {
      // A native action can be removed between a collection notification and
      // this derived export. Do not let one stale wrapper hide the remaining
      // live actions or trigger an export failure loop.
      return null;
    }
  }

  internal static AgentAction ProjectAction(int id, ActionCandidate candidate)
  {
    var action = candidate.Action;
    IReadOnlyList<TargetSetState>? targets = null;
    NumberRange? number = null;
    IReadOnlyList<ActionChoice>? choices = null;
    IReadOnlyList<int>? order = null;
    IReadOnlyList<DistributionState>? distribution = null;

    if (action is CardAction cardAction)
    {
      List<TargetSet>? targetSets =
        Try<List<TargetSet>>(() => cardAction.Targets.ToList());
      targets = (targetSets ?? [])
        .Select((targetSet, index) => ProjectTargetSet(index, targetSet))
        .ToList();
      if (targets.Count == 0)
        targets = null;
    }

    switch (action)
    {
      // Every payload read degrades to null rather than dropping the whole
      // action: a stale remote read must never hide a pending decision.
      case NumericAction numeric:
        number = Try<NumberRange>(() => new NumberRange(
          numeric.Minimum, numeric.Maximum, numeric.Initial));
        break;
      case SelectPlayerAction selectPlayer:
        choices = Try<List<ActionChoice>>(() => selectPlayer.AvailablePlayers
          .OfType<GamePlayer>()
          .ToList()
          .Select(player => new ActionChoice(
            Try<int>(() => player.UserId),
            Try<string>(() => player.Name) ?? string.Empty))
          .ToList());
        break;
      case SelectFromListAction selectFromList:
        choices = Try<List<ActionChoice>>(() => selectFromList.AvailableItems.ToList()
          .Select(item => new ActionChoice(item.Value, item.Name))
          .ToList());
        break;
      case CardSelectorAction cardSelector:
        choices = Try<List<ActionChoice>>(() => cardSelector.Choices
          .Where(choice => choice.CanChoose)
          .Select(choice => new ActionChoice(choice.CTN, $"Card {choice.CTN}"))
          .ToList());
        break;
      case OrderingAction ordering:
        order = Try<List<int>>(() => ordering.OrderedTargets.ToList()
          .Select(card => card.Id)
          .ToList());
        break;
      case CombatDamageAssignmentAction damage:
        distribution = Try<List<DistributionState>>(() => damage.Distributions.ToList()
          .Select(item => new DistributionState(
            ProjectTarget(item.Target),
            item.Minimum,
            item.Maximum,
            item.Value
          ))
          .ToList());
        break;
    }

    // Totals constrain distribution-style actions (damage assignment,
    // distributing counters/damage). Initial carries the floor: at least
    // this much must be assigned across targets.
    NumberRange? distributionTotal = null;
    if (action is CombatDamageAssignmentAction combatAssign)
    {
      distributionTotal = Try<NumberRange>(() => new NumberRange(
        combatAssign.MinimumTotal,
        combatAssign.MaximumTotal,
        combatAssign.MinimumTotal));
    }
    else if (action is DistributingCardAction distributing)
    {
      distributionTotal = Try<NumberRange>(() => new NumberRange(
        distributing.MinimumTotal,
        distributing.MaximumTotal,
        distributing.MinimumTotal));
    }

    return new AgentAction(
      id,
      Try<string>(() => action.Type.ToString()) ?? action.GetType().Name,
      Try<string>(() => action.Name) ?? string.Empty,
      candidate.Source,
      targets,
      number,
      choices,
      order,
      distribution,
      distributionTotal
    );
  }

  internal static TargetSetState ProjectTargetSet(int index, TargetSet targetSet)
  {
    return new TargetSetState(
      index,
      Try<string?>(() => targetSet.Description),
      Try<int>(() => targetSet.MinimumTargets),
      Try<int>(() => targetSet.MaximumTargets),
      (Try<List<Targetable>>(() => targetSet.LegalTargets.ToList())
          is List<Targetable> legal
          ? legal.Select(ProjectTarget).ToList()
          : []),
      (Try<List<Targetable>>(() => targetSet.CurrentTargets.ToList())
          is List<Targetable> current
          ? current.Select(ProjectTarget).ToList()
          : [])
    );
  }

  internal static GameObjectState ProjectTarget(Targetable target)
  {
    var id = Try<int>(() => target.Id);
    var fallback = Try<string?>(() => target.TargetInformation) ?? $"Object {id}";
    object? value = Try<object>(() => target.ToGameObject());
    return value switch
    {
      GameCard card => new GameObjectState("card", id, card.Name),
      GamePlayer player => new GameObjectState("player", id, player.Name),
      _ => new GameObjectState("object", id, fallback)
    };
  }


  internal static List<GameAction> ReadPromptActions(GamePrompt prompt) =>
    (Try<List<GameAction>>(() => prompt.Options.Values
      .SelectMany(actions => actions)
      .Where(action => action is not null)
      .ToList()) ?? new List<GameAction>());

}

internal sealed record ActionCandidate(GameAction Action, ActionSource Source);
