using System.Text.RegularExpressions;

using AlphaMTG.MTGOAgent.UIA.Models;

namespace AlphaMTG.MTGOAgent.UIA;

internal sealed class ActionExecutor(string statePath) : IDisposable
{
  private readonly Inspector _inspector = new();

  public ActionExecution Execute(
    int gameId,
    int nonce,
    int actionId,
    TargetSelection? target = null,
    int? choiceId = null
  )
  {
    var state = AgentStateStore.Read(statePath);
    if (state.GameId != gameId)
      throw new InvalidOperationException(
        $"Game {gameId} is stale; the current game is {state.GameId}."
      );
    if (state.Nonce != nonce)
      throw new InvalidOperationException(
        $"State {nonce} is stale; the current nonce is {state.Nonce}."
      );

    var action = state.Actions.SingleOrDefault(action => action.Id == actionId)
      ?? throw new ArgumentException($"Unknown action {actionId}.");

    if (target is not null && choiceId is not null)
      throw new ArgumentException("A target and a choice cannot be supplied together.");
    if (target is not null)
      return SelectTarget(state, action, target);
    if (choiceId is not null)
      return SelectChoice(state, action, choiceId.Value);
    if (action.Choices is not null)
      throw new ArgumentException("This action requires --choice-id.");

    var snapshot = _inspector.Capture(state);
    string source;
    string? materializedBy = null;

    if (action.Source.Type == "card")
    {
      var cardId = action.Source.CardId
        ?? throw new InvalidOperationException("The card action has no source card.");
      var card = ExactlyOne(
        snapshot.Nodes.Where(node => node.CardId == cardId),
        $"visible UI card for card {cardId}"
      );
      source = $"card:{cardId}";
      var initialPopupCount = _inspector.PopupCount;
      if (initialPopupCount == 0)
      {
        snapshot = _inspector.Perform(
          snapshot,
          card,
          "rightClick",
          state,
          () => _inspector.PopupCount > initialPopupCount
        );
        materializedBy = "rightClick";
      }
    }
    else if (action.Source.Type == "prompt")
    {
      source = "prompt";
    }
    else
    {
      throw new NotSupportedException(
        $"Action source '{action.Source.Type}' is not supported."
      );
    }

    var control = FindControl(
      snapshot,
      action.Name,
      popupOnly: materializedBy is not null
    );
    var operation = control.Operations.Contains("invoke", StringComparer.Ordinal)
      ? "invoke"
      : "click";
    var popupCount = _inspector.PopupCount;
    _inspector.Perform(
      snapshot,
      control,
      operation,
      state,
      control.Root.StartsWith("popup:", StringComparison.Ordinal)
        ? () => _inspector.PopupCount < popupCount
        : null
    );

    return Finish(
      state,
      action,
      new Interaction(
        source,
        materializedBy,
        control.Name ?? action.Name,
        operation
      )
    );
  }

  private ActionExecution Finish(
    AgentState state,
    AgentAction action,
    Interaction interaction
  )
  {
    var nextState = WaitForNextState(state);
    var needsInput = action.Targets is not null
      || action.Number is not null
      || action.Choices is not null
      || action.Order is not null
      || action.Distribution is not null;
    var status = needsInput
      ? "awaitingChoice"
      : nextState.Nonce != state.Nonce
        ? "completed"
        : "submitted";

    return new ActionExecution(
      status,
      action,
      interaction,
      nextState
    );
  }

  private ActionExecution SelectChoice(
    AgentState state,
    AgentAction action,
    int choiceId
  )
  {
    if (action.Type != "ChoosePlayer" || action.Choices is null)
      throw new NotSupportedException(
        $"UI mapping for {action.Type} choices has not been implemented."
      );
    var choice = action.Choices.SingleOrDefault(item => item.Id == choiceId)
      ?? throw new InvalidOperationException(
        $"Choice {choiceId} is not available for action {action.Id}."
      );

    var snapshot = _inspector.Capture(state);
    var control = ExactlyOne(
      snapshot.Nodes.Where(node =>
        node.Player?.Id == choiceId
        && node.IsEnabled),
      $"UI player control for choice {choiceId}"
    );
    var operation = control.Operations.Contains("invoke", StringComparer.Ordinal)
      ? "invoke"
      : "click";
    _inspector.Perform(snapshot, control, operation, state);
    var nextState = WaitForNextState(state);
    var status = nextState.Nonce == state.Nonce
      ? "awaitingChoice"
      : "completed";

    return new ActionExecution(
      status,
      action,
      new Interaction($"player:{choiceId}", null, choice.Name, operation),
      nextState
    );
  }

  private ActionExecution SelectTarget(
    AgentState state,
    AgentAction action,
    TargetSelection selection
  )
  {
    var targetSet = action.Targets?.SingleOrDefault(set =>
      set.Index == selection.Set)
      ?? throw new InvalidOperationException(
        $"Action {action.Id} has no target set {selection.Set}."
      );
    var target = targetSet.Legal.SingleOrDefault(target =>
      target.Id == selection.Id)
      ?? throw new InvalidOperationException(
        $"Object {selection.Id} is not legal for target set {selection.Set}."
      );
    if (target.Type != "card")
      throw new NotSupportedException(
        $"UI mapping for {target.Type} targets has not been implemented."
      );

    var cardId = state.Cards.SingleOrDefault(card =>
      card.Id == target.Id || card.SourceId == target.Id)?.Id
      ?? throw new InvalidOperationException(
        $"Target card {target.Id} is not in the current game state."
      );
    var snapshot = _inspector.Capture(state);
    var card = ExactlyOne(
      snapshot.Nodes.Where(node => node.CardId == cardId),
      $"visible UI card for target {target.Id}"
    );
    _inspector.Perform(snapshot, card, "click", state);
    var nextState = WaitForNextState(state);
    var status = nextState.Nonce == state.Nonce
      ? "awaitingChoice"
      : "completed";

    return new ActionExecution(
      status,
      action,
      new Interaction($"card:{cardId}", null, target.Name, "click"),
      nextState
    );
  }

  private static Node FindControl(
    Snapshot snapshot,
    string label,
    bool popupOnly
  )
  {
    var scope = snapshot.Nodes.Where(node =>
      !popupOnly || node.Root.StartsWith("popup:", StringComparison.Ordinal));
    var labels = scope.Where(node =>
      node.IsEnabled && SameLabel(node.Name, label)).ToList();
    var controls = new List<Node>();

    foreach (var node in labels)
    {
      var control = scope
        .Where(candidate =>
          candidate.Root == node.Root
          && candidate.IsEnabled
          && IsAncestor(candidate.Path, node.Path)
          && (candidate.Operations.Contains("invoke", StringComparer.Ordinal)
            || candidate.Operations.Contains("click", StringComparer.Ordinal)))
        .OrderByDescending(candidate => candidate.Path.Count)
        .FirstOrDefault();
      if (control is not null)
        controls.Add(control);
    }

    return ExactlyOne(
      controls.DistinctBy(control => control.Reference),
      $"enabled control named '{label}'"
    );
  }

  private AgentState WaitForNextState(AgentState before)
  {
    var timeoutText = Environment.GetEnvironmentVariable(
      "ALPHAMTG_ACTION_TIMEOUT_MS"
    );
    var timeout = int.TryParse(timeoutText, out var milliseconds)
      ? milliseconds
      : 3000;
    var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
    var current = before;

    while (DateTime.UtcNow < deadline)
    {
      Thread.Sleep(100);
      current = AgentStateStore.Read(statePath);
      if (current.GameId != before.GameId || current.Nonce != before.Nonce)
        return current;
    }

    return current;
  }

  private static T ExactlyOne<T>(IEnumerable<T> values, string description)
  {
    var matches = values.Take(2).ToList();
    return matches.Count switch
    {
      1 => matches[0],
      0 => throw new InvalidOperationException($"Could not find {description}."),
      _ => throw new InvalidOperationException($"Found multiple matches for {description}.")
    };
  }

  private static bool SameLabel(string? actual, string expected)
  {
    if (actual is null)
      return false;
    if (string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
      return true;

    var withoutSymbols = Regex.Replace(expected, @"\{[^}]+\}", " ");
    return string.Equals(
      NormalizeLabel(actual),
      NormalizeLabel(withoutSymbols),
      StringComparison.OrdinalIgnoreCase
    );
  }

  private static string NormalizeLabel(string value) =>
    string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

  private static bool IsAncestor(
    IReadOnlyList<int> ancestor,
    IReadOnlyList<int> path
  ) =>
    ancestor.Count <= path.Count
    && path.Take(ancestor.Count).SequenceEqual(ancestor);

  public void Dispose() => _inspector.Dispose();
}
