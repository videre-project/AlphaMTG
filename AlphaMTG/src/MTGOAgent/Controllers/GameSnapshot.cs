/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using MTGOSDK.API.Interface.Controls;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors.Events;
using static MTGOSDK.Core.Reflection.DLRWrapper;


namespace AlphaMTG.MTGOAgent.Controllers;

internal static class GameSnapshot
{
  private static readonly MagicColors[] s_manaColors =
  [
    MagicColors.White, MagicColors.Blue, MagicColors.Black,
    MagicColors.Red, MagicColors.Green, MagicColors.Colorless
  ];

  internal static IEnumerable<MagicColors> ManaChoices(ManaPoolAction action)
  {
    MagicColors available = action.ManaPoolColors;
    return s_manaColors.Where(color => (available & color) == color);
  }

  /// <summary>
  /// Assigns consecutive IDs to the options in one captured SDK update.
  /// Snapshot and execution use the same ordering under the update's nonce.
  /// </summary>
  internal static (IPromptOption Option, int? CardId, ulong? LogicalAddress)[] Actions(
    GameStateChangedEventArgs update)
  {
    var actions = new List<(IPromptOption Option, int? CardId, ulong? LogicalAddress)>();
    foreach (IPromptOption option in update.Options.Options.Values.SelectMany(group => group))
    {
      if (option is PromptControl { Enabled: false }) continue;
      if (option is not GameAction and not PromptControl)
        throw new NotSupportedException($"Unsupported SDK prompt option: {option.GetType().Name}.");
      actions.Add((option, (option as CardAction)?.Card.Id, null));
    }

    foreach (var card in update.Interaction.Cards.Values.Where(card =>
      update.Snapshot.Cards.ContainsKey(card.CardId) || update.Snapshot.HiddenCards.ContainsKey(card.CardId)))
    {
      foreach (CardAction action in card.Actions)
        actions.Add((action, card.CardId, card.LogicalAddress));
    }
    return actions.ToArray();
  }

  /// <summary>
  /// Selects detached SDK values explicitly so JSON serialization cannot invoke remote getters.
  /// </summary>
  internal static object Create(
    int gameId,
    GameStateChangedEventArgs update,
    IReadOnlyList<(IPromptOption Option, int? CardId, ulong? LogicalAddress)> actions,
    bool canAct)
  {
    var snapshot = update.Snapshot;
    var interaction = update.Interaction;
    var selections = new List<object>();

    foreach (var card in interaction.Cards.Values)
    {
      var flags = card.Selection;
      if (flags.IsSelectable || flags.IsTargetable || flags.IsTargeted)
        selections.Add(new
        {
          kind = "card",
          cardId = card.CardId,
          logicalAddress = card.LogicalAddress,
          name = (snapshot.Cards.GetValueOrDefault(card.CardId)
            ?? snapshot.HiddenCards.GetValueOrDefault(card.CardId))?.Name,
          selectable = flags.IsSelectable,
          targetable = flags.IsTargetable,
          targeted = flags.IsTargeted
        });
    }

    foreach (var player in interaction.Players)
    {
      if (player.IsTargetable || player.IsTargeted)
        selections.Add(new
        {
          kind = "player",
          playerIndex = player.PlayerIndex,
          name = snapshot.Players.GetValueOrDefault(player.PlayerIndex)?.Name,
          targetable = player.IsTargetable,
          targeted = player.IsTargeted
        });
    }

    return new
    {
      capturedAtUtc = snapshot.ClientTimestamp.ToUniversalTime(),
      gameId,
      nonce = update.Nonce,
      turn = snapshot.TurnNumber,
      phase = snapshot.CurrentPhase.ToString(),
      prompt = new
      {
        text = interaction.PromptBoxText ?? update.Prompt.Text,
        promptedPlayer = snapshot.Players.GetValueOrDefault(update.Prompt.PromptedPlayer)?.Name,
        nativeText = update.Prompt.Text
      },
      players = snapshot.Players.OrderBy(entry => entry.Key).Select(entry => new
      {
        index = entry.Key,
        name = entry.Value.Name,
        life = entry.Value.Life,
        hasPriority = entry.Value.HasPriority
      }).ToArray(),
      cards = snapshot.Cards.Values.Concat(snapshot.HiddenCards.Values).Select(card => new
      {
        id = card.Id,
        name = card.Name,
        zone = card.Zone?.Name,
        ownerIndex = card.OwnerIndex
      }).OrderBy(card => card.zone ?? string.Empty)
        .ThenBy(card => card.ownerIndex).ThenBy(card => card.id).ToArray(),
      actions = actions.Select((entry, id) => Action(id, entry.Option,
        entry.CardId, entry.LogicalAddress, update.Options.ClientRevision)).ToArray(),
      serverNonce = snapshot.Nonce,
      interactionMode = interaction.Mode.ToString(),
      selections = selections.ToArray(),
      canAct
    };
  }

  private static object Action(
    int id, IPromptOption option, int? cardId, ulong? logicalAddress, long clientRevision)
  {
    object source = option switch
    {
      CardAction => new { type = "card", cardId, logicalAddress },
      PromptControl control => new
      {
        type = "control",
        controlId = control.Id,
        clientRevision,
        buttonType = control.ButtonType,
        color = control.Color
      },
      _ => new { type = "prompt" }
    };
    object[]? targets = option is CardAction cardAction
      ? cardAction.Targets.Select((set, index) => (object)new
        {
          index,
          description = set.Description,
          minimum = set.MinimumTargets,
          maximum = set.MaximumTargets,
          distributionTotal = set.IsDistributedAmongTargets ? (int?)set.TotalToAssign : null,
          distributions = set.IsDistributedAmongTargets
            ? set.Distributions.Select(item => new
              {
                target = Target(item.Target), minimum = item.Minimum,
                maximum = item.Maximum, value = item.Value
              }).ToArray()
            : null,
          legal = set.LegalTargets.Select(Target).ToArray(),
          selected = set.CurrentTargets.Select(Target).ToArray()
        }).ToArray()
      : null;

    return new
    {
      id,
      type = option.Type.ToString(),
      name = option.Name,
      source,
      targets = targets is { Length: > 0 } ? targets : null,
      number = option is NumericAction numeric ? Range(numeric.Minimum, numeric.Maximum, numeric.Initial) : null,
      choices = option switch
      {
        ManaPoolAction mana => (object)ManaChoices(mana)
          .Select(color => new { id = (int)color, name = color.ToString() }).ToArray(),
        SelectPlayerAction players => players.AvailablePlayers
          .Select(player => new { id = player.UserId, name = player.Name }).ToArray(),
        SelectFromListAction list => list.AvailableItems
          .Select(item => new { id = item.Value, name = item.Name }).ToArray(),
        CardSelectorAction cards => cards.Choices.Where(choice => choice.CanChoose)
          .Select(choice => new { id = choice.CTN, name = $"Card {choice.CTN}" }).ToArray(),
        _ => null
      },
      order = option is OrderingAction ordering ? ordering.OrderedTargets.Select(card => card.Id).ToArray() : null,
      distribution = option is CombatDamageAssignmentAction damage
        ? damage.Distributions.Select(item => new
          {
            target = Target(item.Target),
            minimum = item.Minimum,
            maximum = item.Maximum,
            value = item.Value
          }).ToArray()
        : null,
      distributionTotal = option switch
      {
        CombatDamageAssignmentAction damageAction =>
          Range(damageAction.MinimumTotal, damageAction.MaximumTotal, damageAction.MinimumTotal),
        DistributingCardAction distributing =>
          Range(distributing.MinimumTotal, distributing.MaximumTotal, distributing.MinimumTotal),
        _ => null
      }
    };
  }

  private static object Range(int minimum, int maximum, int initial) => new { minimum, maximum, initial };

  private static object Target(Targetable target)
  {
    int id = target.Id;
    object? value = Try<object>(() => target.ToGameObject());
    return value switch
    {
      GameCard card => new { type = "card", id, name = card.Name },
      GamePlayer player => new { type = "player", id, name = player.Name },
      _ => new { type = "object", id, name = Try<string?>(() => target.TargetInformation) ?? $"Object {id}" }
    };
  }
}
