/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Collections;

using MTGOSDK.API.Play.Games;
using static MTGOSDK.Core.Reflection.DLRWrapper;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Batching;
using MTGOSDK.Core.Remoting.Interop;
using MTGOSDK.Core.Remoting.Interop.Interactions.Batching;


namespace AlphaMTG.MTGOAgent.Controllers;

/// <summary>
/// Prepares and submits MTGO model actions for requested game interactions.
/// </summary>
internal static class GameActionExecution
{
  public static void Execute(Game game, GameAction action)
  {
    ArgumentNullException.ThrowIfNull(action);
    using var scope = DiverCommunicator.BeginUIThreadScope();
    Unbind(game).ExecuteAction(Unbind(action));
  }

  public static void SelectColor(ManaPoolAction action, MagicColors color)
  {
    int bits = (int)color;
    if (bits <= 0 || (bits & (bits - 1)) != 0 || (action.ManaPoolColors & color) != color)
      throw new ArgumentException($"Color '{color}' is not available.", nameof(color));

    using var scope = DiverCommunicator.BeginUIThreadScope();
    Unbind(action).SelectedItem = RemoteClient.CreateEnum(
      "WotC.MtGO.Client.Model.MagicColors", color.ToString());
  }

  public static void SelectItem(SelectFromListAction action, int value)
  {
    var batch = new RemoteBatch { ForceUIThread = true };
    var items = batch.Read(batch.Input((object)Unbind(action)), nameof(SelectFromListAction.AvailableItems));
    var matching = batch.Where(items, "Value", ComparisonOperator.Equal, batch.Input(value));
    var matches = batch.ExecuteReference(matching, RemoteClient.client)!.Dynamify();
    dynamic selected = Map<dynamic>((object)matches).SingleOrDefault()
      ?? throw new ArgumentException($"Value {value} is not available.", nameof(value));

    using var scope = DiverCommunicator.BeginUIThreadScope();
    Unbind(action).SelectedValue = selected;
  }

  public static void SelectCard(CardSelectorAction action, int textureNumber)
  {
    var batch = new RemoteBatch { ForceUIThread = true };
    var choices = batch.Read(batch.Input((object)Unbind(action)), nameof(CardSelectorAction.Choices));
    var matching = batch.Where(choices, "CTN", ComparisonOperator.Equal, batch.Input(textureNumber));
    var allowed = batch.Where(matching, "CanChoose", ComparisonOperator.Equal, batch.Input(true));
    batch.Output("count", batch.Count(allowed));
    if (batch.Execute(RemoteClient.client).Values["count"].GetScalar<int>() != 1)
      throw new ArgumentException($"Card {textureNumber} is not an available unique choice.", nameof(textureNumber));

    using var scope = DiverCommunicator.BeginUIThreadScope();
    Unbind(action).SelectedCard = textureNumber;
  }

  public static void SetOrder(OrderingAction action, IReadOnlyList<int> cardIds)
  {
    ArgumentNullException.ThrowIfNull(cardIds);
    object model = Unbind(action);
    var read = new RemoteBatch { ForceUIThread = true };
    read.ForEach(read.Read(read.Input(model), nameof(OrderingAction.OrderedTargets)),
      body => body.Output("id", body.Read("Id")));
    int[] existing = read.Execute(RemoteClient.client).Rows
      .Select(row => row.Values["id"].GetScalar<int>()).ToArray();
    int[] requested = cardIds.ToArray();
    if (requested.Length != existing.Length || requested.Distinct().Count() != requested.Length
      || !existing.ToHashSet().SetEquals(requested))
      throw new ArgumentException("Supply every ordered target's card ID exactly once.", nameof(cardIds));

    var batch = new RemoteBatch { ForceUIThread = true };
    var targets = batch.Read(batch.Input(model), nameof(OrderingAction.OrderedTargets));
    var list = RemoteBatchType.From(typeof(IList));
    var integer = RemoteBatchType.From(typeof(int));
    var obj = RemoteBatchType.From(typeof(object));
    var dictionary = RemoteBatchType.From(typeof(Dictionary<int, object>));
    var lookup = batch.Create(new(dictionary, ".ctor"));
    // Resolve by ID in the mutation request so an intervening UI reorder
    // cannot change which card each requested ID identifies.
    batch.ForEach(targets, body =>
      body.Call(lookup, new(dictionary, "Add", integer, obj), body.Read("Id"), body.Item));
    var cards = requested.Select(id =>
      batch.Call(lookup, new(dictionary, "get_Item", integer), batch.Input(id))).ToArray();
    batch.Call(targets, new(list, nameof(IList.Clear)));
    foreach (var card in cards)
      batch.Call(targets, new(list, nameof(IList.Add), RemoteBatchType.From(typeof(object))), card);
    batch.Execute(RemoteClient.client);
  }

  public static void SetDistribution(CombatDamageAssignmentAction action,
    IReadOnlyDictionary<int, int> amounts)
  {
    SetValues(Unbind(action).Distributions, amounts, action.MinimumTotal, action.MaximumTotal);
  }

  public static void SetDistribution(TargetSet set, IReadOnlyDictionary<int, int> amounts)
  {
    if (!set.IsDistributedAmongTargets)
      throw new InvalidOperationException("This target set does not distribute amounts.");
    SetValues(Unbind(set).Distributions.Values, amounts, set.TotalToAssign, set.TotalToAssign);
  }

  private static void SetValues(object collection,
    IReadOnlyDictionary<int, int> amounts, int minimumTotal, int maximumTotal)
  {
    ArgumentNullException.ThrowIfNull(amounts);
    var read = new RemoteBatch { ForceUIThread = true };
    var scalar = RemoteBatchProjection.Value();
    read.Output("targets", read.Input(collection), RemoteBatchProjection.Sequence(
      RemoteBatchProjection.Object(
        ("Id", "Target.Id", scalar), ("Minimum", "Minimum", scalar),
        ("Maximum", "Maximum", scalar))));
    var targets = read.Execute(RemoteClient.client).Values["targets"].Items.ToDictionary(
      item => item.Members["Id"].GetScalar<int>(),
      item => (Minimum: item.Members["Minimum"].GetScalar<int>(),
               Maximum: item.Members["Maximum"].GetScalar<int>()));
    if (amounts.Count != targets.Count || amounts.Keys.Any(id => !targets.ContainsKey(id)))
      throw new ArgumentException("Supply an amount for every distribution target exactly once.", nameof(amounts));
    long total = amounts.Values.Sum(value => (long)value);
    if (total < minimumTotal || total > maximumTotal)
      throw new ArgumentException($"The total must be within [{minimumTotal}, {maximumTotal}].", nameof(amounts));
    foreach (var entry in amounts)
    {
      var range = targets[entry.Key];
      if (entry.Value < range.Minimum || entry.Value > range.Maximum)
        throw new ArgumentException($"Amount {entry.Value} is outside the range for target {entry.Key}.", nameof(amounts));
    }

    // Apply all validated amounts in one request, matching live targets by ID.
    var batch = new RemoteBatch { ForceUIThread = true };
    var integer = RemoteBatchType.From(typeof(int));
    var dictionary = RemoteBatchType.From(typeof(Dictionary<int, int>));
    var values = batch.Create(new(dictionary, ".ctor"));
    foreach (var entry in amounts)
      batch.Call(values, new(dictionary, "Add", integer, integer),
        batch.Input(entry.Key), batch.Input(entry.Value));
    batch.ForEach(batch.Input(collection), body =>
    {
      var amount = body.Call(values, new(dictionary, "get_Item", integer), body.Read("Target.Id"));
      body.Call(body.Item, new(new("WotC.MtGO.Client.Model.Play.IDistribution"), "set_Value", integer), amount);
    });
    batch.Execute(RemoteClient.client);
  }
}
