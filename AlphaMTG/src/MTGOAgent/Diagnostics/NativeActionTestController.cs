/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
 **/

using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using MTGOSDK.API;
using MTGOSDK.API.Play;
using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Leagues;
using MTGOSDK.API.Play.Tournaments;
using MTGOSDK.Core.Reflection;
using MTGOSDK.Core.Remoting;
using MTGOSDK.Core.Remoting.Interop;


namespace AlphaMTG.MTGOAgent.Diagnostics;

/// <summary>
/// Exercises a live MTGO action after the request supplies the confirmation token.
/// </summary>
internal sealed class NativeActionTestController : IDisposable
{
  private const string GameType =
    "WotC.MtGO.Client.Model.Play.InProgressGameEvent.InProgressGame";
  private const string CardViewModelType =
    "Shiny.Play.Duel.ViewModel.DuelSceneCardViewModel";
  private const string DuelSceneViewModelType =
    "Shiny.Play.Duel.ViewModel.DuelSceneViewModel";
  private const string ExpectedConfirmation = "execute";

  private readonly ILogger _logger;
  private readonly string _requestPath;
  private readonly string _resultPath;
  private readonly CancellationTokenSource _stop = new();
  private readonly object _writeLock = new();
  private Task? _worker;
  private int _disposed;

  public NativeActionTestController(ILogger logger)
  {
    _logger = logger;
    _requestPath = Environment.GetEnvironmentVariable(
      "ALPHAMTG_NATIVE_ACTION_TEST_REQUEST")
      ?? Path.Combine("AlphaMTG", "native-action-test.request.json");
    _resultPath = Environment.GetEnvironmentVariable(
      "ALPHAMTG_NATIVE_ACTION_TEST_RESULT")
      ?? Path.Combine("AlphaMTG", "native-action-test.result.json");
  }

  public void Start()
  {
    if (_worker is not null)
      return;

    _worker = Task.Run(RunAsync);
    _logger.LogInformation(
      "Native action test controller is watching {RequestPath}.",
      _requestPath);
  }

  private async Task RunAsync()
  {
    while (!_stop.IsCancellationRequested)
    {
      try
      {
        if (File.Exists(_requestPath))
        {
          string json = await File.ReadAllTextAsync(
            _requestPath,
            _stop.Token);
          NativeActionTestRequest? request =
            JsonSerializer.Deserialize<NativeActionTestRequest>(
              json,
              AgentJson.Options);

          // Consume the request before executing it. A failed invocation must
          // never be retried automatically against a later prompt.
          File.Delete(_requestPath);

          if (request is null)
          {
            WriteResult(NativeActionTestResult.Failure(
              null,
              "The native action test request was empty."));
          }
          else if (!string.Equals(
            request.Confirm,
            ExpectedConfirmation,
            StringComparison.Ordinal))
          {
            WriteResult(NativeActionTestResult.Failure(
              request,
              "The request did not contain the exact confirmation token "
                + $"'{ExpectedConfirmation}'."));
          }
          else
          {
            NativeActionTestResult result = await ExecuteAsync(request);
            WriteResult(result);
          }
        }
      }
      catch (OperationCanceledException) when (_stop.IsCancellationRequested)
      {
        break;
      }
      catch (Exception exception)
      {
        _logger.LogError(
          exception,
          "Native action test controller failed while processing a request.");
        try
        {
          WriteResult(NativeActionTestResult.Failure(
            null,
            exception.ToString()));
        }
        catch
        {
          // There is no useful recovery path if the result file itself is
          // unavailable.
        }
      }

      try
      {
        await Task.Delay(200, _stop.Token);
      }
      catch (OperationCanceledException) when (_stop.IsCancellationRequested)
      {
        break;
      }
    }
  }

  private async Task<NativeActionTestResult> ExecuteAsync(
    NativeActionTestRequest request)
  {
    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    DateTimeOffset deadline = startedAt.AddSeconds(60);
    string? lastReadiness = null;

    while (DateTimeOffset.UtcNow < deadline && !_stop.IsCancellationRequested)
    {
      try
      {
        Game? game = FindGame(request.GameId);
        if (game is null)
        {
          lastReadiness = $"Game {request.GameId} was not found.";
        }
        else
        {
          NativeActionTestResult? result = request.ConfirmPrompt
            ? TryConfirmPrompt(game, request)
            : TryExecute(game, request);
          if (result is not null)
            return result;

          lastReadiness = request.ConfirmPrompt
            ? "The native prompt confirmation is not ready yet."
            : "The requested live action is not ready yet.";
        }
      }
      catch (Exception exception)
      {
        lastReadiness = exception.ToString();
      }

      try
      {
        await Task.Delay(250, _stop.Token);
      }
      catch (OperationCanceledException) when (_stop.IsCancellationRequested)
      {
        break;
      }
    }

    return NativeActionTestResult.Failure(
      request,
      "Timed out waiting for the requested live action. "
        + (lastReadiness ?? "No readiness detail was reported."));
  }

  private NativeActionTestResult? TryConfirmPrompt(
    Game game,
    NativeActionTestRequest request)
  {
    GamePrompt? prompt = game.Prompt;
    if (prompt is null)
      return null;
    if (request.ExpectedPromptContains is not null
      && !prompt.Text.Contains(
        request.ExpectedPromptContains,
        StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    GameCard? card = request.CardId > 0
      ? game.GetGameCard(request.CardId)
      : null;
    if (request.CardId > 0 && card is null)
      return null;
    if (request.CardId > 0 && request.ExpectedAttackVictimId is not null
      && card?.IsAttacking != true)
    {
      return null;
    }

    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    NativeActionTestSnapshot before = Capture(game, card);
    object? scene = FindScene(game.Id);
    if (scene is null)
      return null;

    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic sceneViewModel = (dynamic)scene;
      dynamic promptBox = sceneViewModel.PromptBox;
      dynamic okButton = promptBox.OkPromptButton;
      if (okButton is null)
        return null;

      string buttonName = Convert.ToString(okButton.Name) ?? string.Empty;
      bool enabled = Convert.ToBoolean(okButton.Enabled);
      bool visible = Convert.ToBoolean(okButton.Visible);
      if (!string.Equals(buttonName, "OK", StringComparison.Ordinal)
        || !enabled || !visible)
      {
        throw new InvalidOperationException(
          $"The native prompt OK button is not ready: name='{buttonName}', "
            + $"enabled={enabled}, visible={visible}.");
      }

      _logger.LogInformation(
        "Invoking native prompt OK for game {GameId} after prompt: {Prompt}",
        request.GameId,
        prompt.Text);
      okButton.OnClick();
    }

    NativeActionTestSnapshot after = WaitForChange(
      game,
      request.CardId,
      before,
      TimeSpan.FromSeconds(10));
    bool changed = after.PromptNonce != before.PromptNonce
      || after.Turn != before.Turn
      || !string.Equals(after.Phase, before.Phase, StringComparison.Ordinal)
      || !string.Equals(after.PromptText, before.PromptText, StringComparison.Ordinal);
    return new NativeActionTestResult(
      changed ? "confirmed" : "submitted",
      request.GameId,
      request.CardId,
      "OK",
      "PromptButton",
      null,
      0,
      before.IsAttacking,
      before,
      after,
      before.PromptText,
      changed ? null : "The native OK click returned without a prompt transition.",
      startedAt,
      DateTimeOffset.UtcNow);
  }

  private static object? FindScene(int gameId)
  {
    Game? game = FindGame(gameId);
    return game?.DuelScene;
  }

  private static NativeViewModelActionSelection? FindNativeViewModelAction(
    GameCard liveCard,
    NativeActionTestRequest request)
  {
    using (DiverCommunicator.BeginUIThreadScope())
    {
      object? cardViewModel = ResolveCardViewModel(liveCard);
      if (cardViewModel is null)
        return null;

      IReadOnlyList<NativeCardActionCandidate> actions =
        ReadNativeViewModelActions(cardViewModel);
      List<NativeCardActionCandidate> matches = actions
        .Where(action =>
          string.Equals(action.Name, request.ActionName, StringComparison.Ordinal)
          && (request.ExpectedActionType is null
            || string.Equals(
              action.ActionType,
              request.ExpectedActionType,
              StringComparison.Ordinal)))
        .ToList();

      if (matches.Count == 0)
        return null;
      if (matches.Count != 1)
      {
        throw new InvalidOperationException(
          $"Expected one native card-VM action named '{request.ActionName}', "
            + $"but found {matches.Count}.");
      }

      int index = actions.ToList().IndexOf(matches[0]);
      if (request.ActionOrdinal is int ordinal && ordinal != index)
      {
        throw new InvalidOperationException(
          $"The requested native action ordinal {ordinal} resolved to "
            + $"ordinal {index}.");
      }
      if (request.RequireTargetless && matches[0].TargetCount != 0)
      {
        throw new InvalidOperationException(
          $"The native action has {matches[0].TargetCount} target set(s), "
            + "but this request requires a targetless action.");
      }
      if (request.ExpectedAttackVictimId is int victim
        && matches[0].AttackVictimId != victim)
      {
        throw new InvalidOperationException(
          $"The native action targets victim {matches[0].AttackVictimId}, "
            + $"not expected victim {victim}.");
      }

      return new NativeViewModelActionSelection(
        cardViewModel,
        matches[0],
        index);
    }
  }

  private static object? ResolveCardViewModel(GameCard liveCard)
  {
    dynamic rawCard = DLRWrapper.Unbind(liveCard);
    dynamic relationship = rawCard.Tag;
    object? cardViewModel = relationship?.CardViewModel;
    if (cardViewModel is not null)
      return cardViewModel;

    return RemoteClient.InvokeMethod(
      CardViewModelType,
      "GetFromGameCard",
      genericTypes: null,
      args: new object[] { liveCard });
  }

  private static IReadOnlyList<NativeCardActionCandidate> ReadNativeViewModelActions(
    object cardViewModel)
  {
    var result = new List<NativeCardActionCandidate>();
    dynamic viewModel = (dynamic)cardViewModel;
    foreach (dynamic action in viewModel.Actions)
    {
      try
      {
        string name = Convert.ToString(action.Name) ?? string.Empty;
        string actionType = Convert.ToString(action.ActionType) ?? string.Empty;
        int actionId = Convert.ToInt32(action.ActionFlags);
        int targetCount = ReadRemote(() => Convert.ToInt32(action.Targets.Count)) ?? -1;
        bool canBePerformedLocally = ReadRemote(
          () => (bool)action.CanBePerformedLocally);
        bool altMenuAction = ReadRemote(
          () => (bool)action.AltMenuAction);
        int attackVictimId = ReadRemote(
          () => Convert.ToInt32(action.AttackVictimId)) ?? -1;
        result.Add(new NativeCardActionCandidate(
          action,
          name,
          actionType,
          actionId,
          targetCount,
          canBePerformedLocally,
          altMenuAction,
          attackVictimId));
      }
      catch
      {
        // A stale collection member must not hide other valid live actions.
      }
    }
    return result;
  }

  private static T? ReadRemote<T>(Func<T> getter)
  {
    try
    {
      return getter();
    }
    catch
    {
      return default;
    }
  }

  private NativeActionTestResult ExecuteNativeViewModelAction(
    Game game,
    GameCard liveCard,
    NativeActionTestRequest request,
    NativeViewModelActionSelection selection)
  {
    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    NativeActionTestSnapshot before = Capture(game, liveCard);
    NativeCardActionCandidate action = selection.Action;

    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic cardViewModel = (dynamic)selection.CardViewModel;
      dynamic rawAction = (dynamic)action.Action;
      _logger.LogInformation(
        "Invoking native card-VM action for game {GameId}, card {CardId}, "
          + "action {ActionName} ({ActionType}).",
        request.GameId,
        request.CardId,
        action.Name,
        action.ActionType);
      cardViewModel.ExecuteAction(rawAction);
    }

    NativeActionTestSnapshot after;
    try
    {
      after = WaitForChange(
        game,
        request.CardId,
        before,
        TimeSpan.FromSeconds(10));
    }
    catch (Exception exception)
    {
      // The action has already been invoked. Never let a readback failure
      // cause the controller to retry the same live action.
      return new NativeActionTestResult(
        "invokedUnverified",
        request.GameId,
        request.CardId,
        action.Name,
        action.ActionType,
        selection.Index,
        action.TargetCount,
        before.IsAttacking,
        before,
        before,
        before.PromptText,
        "The action returned, but post-invocation readback failed: "
          + exception.Message,
        startedAt,
        DateTimeOffset.UtcNow);
    }

    bool? attackVerified = request.ExpectedAttackVictimId is int
      ? after.IsAttacking == true
      : null;
    string status = attackVerified == false
      ? "invokedUnverified"
      : "invoked";
    string? error = attackVerified == false
      ? "The action returned, but the live card did not enter IsAttacking=true."
      : null;

    return new NativeActionTestResult(
      status,
      request.GameId,
      request.CardId,
      action.Name,
      action.ActionType,
      selection.Index,
      action.TargetCount,
      attackVerified,
      before,
      after,
      before.PromptText,
      error,
      startedAt,
      DateTimeOffset.UtcNow);
  }

  private NativeActionTestResult? TryExecute(
    Game game,
    NativeActionTestRequest request)
  {
    if (game.IsReplay)
      throw new InvalidOperationException("Replay games are not accepted.");

    GamePrompt? prompt = game.Prompt;
    if (prompt is null)
      return null;

    if (request.ExpectedPromptContains is not null
      && !prompt.Text.Contains(
        request.ExpectedPromptContains,
        StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    GameCard? liveCard = game.GetGameCard(request.CardId);
    if (liveCard is null)
      return null;

    string? zone = SafeRead(() => liveCard.Zone?.Name);
    if (request.ExpectedZone is not null
      && !string.Equals(zone, request.ExpectedZone, StringComparison.Ordinal))
    {
      return null;
    }

    NativeViewModelActionSelection? nativeSelection =
      FindNativeViewModelAction(liveCard, request);
    if (nativeSelection is not null)
      return ExecuteNativeViewModelAction(
        game,
        liveCard,
        request,
        nativeSelection);

    List<CardAction> actions = liveCard.Actions.ToList();
    List<CardAction> matches = actions
      .Where(action =>
        string.Equals(action.Name, request.ActionName, StringComparison.Ordinal)
        && (request.ExpectedActionType is null
          || string.Equals(
            action.Type.ToString(),
            request.ExpectedActionType,
            StringComparison.Ordinal)))
      .ToList();

    if (matches.Count != 1)
    {
      throw new InvalidOperationException(
        $"Expected exactly one live action named '{request.ActionName}' "
          + $"on card {request.CardId}, but found {matches.Count}. "
          + $"Available actions: {string.Join(", ", actions.Select(a => a.Name))}.");
    }

    int actionIndex = actions.IndexOf(matches[0]);
    if (request.ActionOrdinal is int ordinal && ordinal != actionIndex)
    {
      throw new InvalidOperationException(
        $"The requested action ordinal {ordinal} resolved to live action "
          + $"ordinal {actionIndex}.");
    }

    CardAction liveAction = matches[0];
    if (liveAction.Type.ToString() != "CardAction")
    {
      throw new InvalidOperationException(
        $"The selected action is {liveAction.Type}, not CardAction.");
    }

    int targetCount = liveAction.Targets.Count;
    if (request.RequireTargetless && targetCount != 0)
    {
      throw new InvalidOperationException(
        $"The selected action has {targetCount} target set(s), but this "
          + "diagnostic request requires a targetless action.");
    }

    DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    NativeActionTestSnapshot before = Capture(game, liveCard);
    object? cardViewModel = null;
    object? rawAction = null;

    // ForceUIThread is important here: the card VM is not itself a
    // DispatcherObject, but its ExecuteAction method expects to run on MTGO's
    // WPF/UI thread just as it does when invoked by Card_View.Item_Click.
    using (DiverCommunicator.BeginUIThreadScope())
    {
      dynamic rawCardObject = DLRWrapper.Unbind(liveCard);
      dynamic relationship = rawCardObject.Tag;
      cardViewModel = relationship?.CardViewModel;

      if (cardViewModel is null)
      {
        cardViewModel = RemoteClient.InvokeMethod(
          CardViewModelType,
          "GetFromGameCard",
          genericTypes: null,
          args: new object[] { liveCard });
      }

      rawAction = DLRWrapper.Unbind(liveAction);
      if (cardViewModel is null)
      {
        throw new InvalidOperationException(
          "The live card has no DuelSceneCardViewModel.");
      }

      dynamic cardViewModelObject = (dynamic)cardViewModel;
      dynamic actionObject = (dynamic)rawAction;
      _logger.LogInformation(
        "Invoking native card VM ExecuteAction for game {GameId}, "
          + "card {CardId}, action {ActionName} ({ActionType}).",
        request.GameId,
        request.CardId,
        liveAction.Name,
        liveAction.Type);
      cardViewModelObject.ExecuteAction(actionObject);
    }

    NativeActionTestSnapshot after = WaitForChange(
      game,
      request.CardId,
      before,
      TimeSpan.FromSeconds(10));

    return new NativeActionTestResult(
      "invoked",
      request.GameId,
      request.CardId,
      request.ActionName,
      liveAction.Type.ToString(),
      actionIndex,
      targetCount,
      null,
      before,
      after,
      prompt.Text,
      null,
      startedAt,
      DateTimeOffset.UtcNow);
  }

  private static NativeActionTestSnapshot WaitForChange(
    Game game,
    int cardId,
    NativeActionTestSnapshot before,
    TimeSpan timeout)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
    NativeActionTestSnapshot current = before;
    do
    {
      GameCard? card = game.GetGameCard(cardId);
      current = Capture(game, card);
      if (current.PromptNonce != before.PromptNonce
        || !string.Equals(current.CardZone, before.CardZone, StringComparison.Ordinal)
        || current.Turn != before.Turn
        || current.IsAttacking != before.IsAttacking
        || current.IsTapped != before.IsTapped
        || !(current.AttackingOrderIds ?? []).SequenceEqual(
          before.AttackingOrderIds ?? [])
        || !string.Equals(current.Phase, before.Phase, StringComparison.Ordinal))
      {
        return current;
      }

      Thread.Sleep(100);
    }
    while (DateTimeOffset.UtcNow < deadline);

    return current;
  }

  private static NativeActionTestSnapshot Capture(
    Game game,
    GameCard? card)
  {
    return new NativeActionTestSnapshot(
      SafeRead(() => game.Prompt?.Nonce),
      SafeRead(() => game.Prompt?.Text),
      SafeRead(() => game.CurrentTurn),
      SafeRead(() => game.CurrentPhase.ToString()),
      SafeRead(() => card?.Id),
      SafeRead(() => card?.Name),
      SafeRead(() => card?.Zone?.Name),
      SafeRead(() => card?.IsAttacking),
      SafeRead(() => card?.IsTapped),
      SafeRead(() => card?.AttackingOrders
        .Select(target => target.Id)
        .ToList()) ?? []);
  }

  private static T? SafeRead<T>(Func<T?> getter)
  {
    try
    {
      return getter();
    }
    catch
    {
      return default;
    }
  }

  private void WriteResult(NativeActionTestResult result)
  {
    lock (_writeLock)
    {
      string? directory = Path.GetDirectoryName(Path.GetFullPath(_resultPath));
      if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

      string temporaryPath = _resultPath + ".tmp";
      File.WriteAllText(
        temporaryPath,
        JsonSerializer.Serialize(result, AgentJson.Options));
      File.Move(temporaryPath, _resultPath, overwrite: true);
    }

    _logger.LogInformation(
      "Native action test result written to {ResultPath}: {Status}.",
      _resultPath,
      result.Status);
  }

  private static Game? FindGame(int gameId)
  {
    foreach (dynamic joinedEvent in EventManager.JoinedEvents)
    {
      try
      {
        Match? match = joinedEvent switch
        {
          League league => league.ActiveMatch,
          Tournament tournament => tournament.ActiveMatch,
          Match directMatch => directMatch,
          _ => null
        };
        Game? currentGame = match?.CurrentGame;
        if (currentGame?.Id == gameId)
          return currentGame;
      }
      catch
      {
        // Ignore stale joined events while the client is transitioning.
      }
    }

    return null;
  }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
      return;

    _stop.Cancel();
    _stop.Dispose();
  }
}

internal sealed record NativeCardActionCandidate(
  object Action,
  string Name,
  string ActionType,
  int ActionId,
  int TargetCount,
  bool CanBePerformedLocally,
  bool AltMenuAction,
  int AttackVictimId);

internal sealed record NativeViewModelActionSelection(
  object CardViewModel,
  NativeCardActionCandidate Action,
  int Index);

internal sealed record NativeActionTestRequest(
  int GameId,
  int CardId,
  string ActionName,
  string? ExpectedActionType = null,
  int? ActionOrdinal = null,
  string? ExpectedZone = null,
  string? ExpectedPromptContains = null,
  bool RequireTargetless = false,
  int? ExpectedAttackVictimId = null,
  bool ConfirmPrompt = false,
  string? Confirm = null);

internal sealed record NativeActionTestSnapshot(
  int? PromptNonce,
  string? PromptText,
  int? Turn,
  string? Phase,
  int? CardId,
  string? CardName,
  string? CardZone,
  bool? IsAttacking = null,
  bool? IsTapped = null,
  IReadOnlyList<int>? AttackingOrderIds = null);

internal sealed record NativeActionTestResult(
  string Status,
  int? GameId,
  int? CardId,
  string? ActionName,
  string? ActionType,
  int? ActionOrdinal,
  int? TargetCount,
  bool? AttackVerified,
  NativeActionTestSnapshot? Before,
  NativeActionTestSnapshot? After,
  string? PromptTextAtInvocation,
  string? Error,
  DateTimeOffset StartedAtUtc,
  DateTimeOffset CompletedAtUtc)
{
  public static NativeActionTestResult Failure(
    NativeActionTestRequest? request,
    string error)
  {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    return new NativeActionTestResult(
      "failed",
      request?.GameId,
      request?.CardId,
      request?.ActionName,
      request?.ExpectedActionType,
      request?.ActionOrdinal,
      null,
      null,
      null,
      null,
      null,
      error,
      now,
      now);
  }
}
