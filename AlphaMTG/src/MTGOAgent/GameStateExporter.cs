/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using MTGOSDK.API.Play.Games;
using MTGOSDK.API.Play.Games.Processors;
using MTGOSDK.API.Play.Games.Processors.EventArgs;


namespace AlphaMTG.MTGOAgent;

internal sealed class GameStateExporter(ILogger logger) : IDisposable
{
  private static readonly JsonSerializerOptions s_jsonOptions = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
  };

  private readonly ConcurrentDictionary<int, Game> _games = new();
  private readonly object _writeLock = new();
  private readonly string _outputPath = Environment.GetEnvironmentVariable(
    "ALPHAMTG_GAME_STATE_PATH"
  ) ?? Path.Combine("AlphaMTG", "FlaUIInspector", "mtgo-game.json");

  public void Start()
  {
    GameProcessor.OnNewGame += AttachGame;
    GameProcessor.EnsureHookInitialized();
    logger.LogInformation("MTGO game-state exporter is ready.");
  }

  private void AttachGame(Game game)
  {
    if (!_games.TryAdd(game.Id, game))
      return;

    game.OnPromptChanged += args => Export(game, args);
    game.ReadyProcessor();
    logger.LogInformation("Tracking MTGO game {GameId}.", game.Id);
  }

  private void Export(Game game, PromptChangedEventArgs args)
  {
    try
    {
      var document = BuildDocument(game, args.Snapshot, args.Prompt);
      var fullPath = Path.GetFullPath(_outputPath);
      Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

      lock (_writeLock)
      {
        var temporaryPath = fullPath + ".tmp";
        File.WriteAllText(
          temporaryPath,
          JsonSerializer.Serialize(document, s_jsonOptions)
        );
        File.Move(temporaryPath, fullPath, overwrite: true);
      }

      logger.LogInformation(
        "Wrote game {GameId} state with {CardCount} visible cards to {Path}.",
        game.Id,
        document.Cards.Count,
        fullPath
      );
    }
    catch (Exception exception)
    {
      logger.LogError(exception, "Failed to export MTGO game {GameId}.", game.Id);
    }
  }

  private static GameDocument BuildDocument(
    Game game,
    GameStateSnapshot snapshot,
    GamePrompt prompt
  )
  {
    var promptActions = ReadPromptActions(prompt);
    var legalTargets = BuildLegalTargetIndex(promptActions);
    var cards = snapshot.Cards.Values
      .OrderBy(card => Read(() => card.Zone?.Name) ?? string.Empty)
      .ThenBy(card => Read(() => card.Id))
      .Select(card => BuildCard(game, card, promptActions, legalTargets))
      .ToList();

    return new GameDocument(
      DateTimeOffset.UtcNow,
      game.Id,
      snapshot.Timestamp,
      snapshot.TurnNumber,
      snapshot.CurrentPhase.ToString(),
      new PromptDocument(
        prompt.Nonce,
        prompt.Text,
        prompt.PromptedPlayer,
        promptActions.Select(ProjectAction).ToList()
      ),
      cards
    );
  }

  private static CardDocument BuildCard(
    Game game,
    GameCard snapshotCard,
    IReadOnlyList<GameAction> promptActions,
    IReadOnlyDictionary<int, List<GameAction>> legalTargets
  )
  {
    var liveCard = Read(() => game.GetGameCard(snapshotCard.Id)) ?? snapshotCard;
    var availableActions = Read(() => liveCard.Actions.ToList()) ?? [];
    var matchingPromptActions = promptActions
      .OfType<CardAction>()
      .Where(action => SameCard(snapshotCard, Read(() => action.Card)))
      .Cast<GameAction>()
      .ToList();

    var targetActions = new List<GameAction>();
    AddTargetActions(legalTargets, snapshotCard.Id, targetActions);
    AddTargetActions(legalTargets, snapshotCard.SourceId, targetActions);

    return new CardDocument(
      snapshotCard.Id,
      snapshotCard.SourceId,
      snapshotCard.CTN,
      snapshotCard.Name,
      Read(() => snapshotCard.Zone?.Name),
      Read(() => snapshotCard.OwnerIndex),
      Read(() => snapshotCard.ControllerIndex),
      availableActions.Select(ProjectAction).ToList(),
      matchingPromptActions.Select(ProjectAction).ToList(),
      targetActions
        .DistinctBy(ActionKey)
        .Select(ProjectAction)
        .ToList()
    );
  }

  private static List<GameAction> ReadPromptActions(GamePrompt prompt)
  {
    return Read(() => prompt.Options.Values.SelectMany(actions => actions).ToList())
      ?? [];
  }

  private static Dictionary<int, List<GameAction>> BuildLegalTargetIndex(
    IReadOnlyList<GameAction> actions
  )
  {
    var index = new Dictionary<int, List<GameAction>>();
    foreach (var action in actions.OfType<CardAction>())
    {
      foreach (var targetSet in Read(() => action.Targets.ToList()) ?? [])
      {
        foreach (var target in Read(() => targetSet.LegalTargets.ToList()) ?? [])
        {
          var targetId = Read(() => target.Id);
          if (targetId == 0)
            continue;

          if (!index.TryGetValue(targetId, out var targetActions))
          {
            targetActions = [];
            index[targetId] = targetActions;
          }
          targetActions.Add(action);
        }
      }
    }
    return index;
  }

  private static void AddTargetActions(
    IReadOnlyDictionary<int, List<GameAction>> index,
    int cardId,
    List<GameAction> actions
  )
  {
    if (cardId > 0 && index.TryGetValue(cardId, out var matches))
      actions.AddRange(matches);
  }

  private static bool SameCard(GameCard card, GameCard? other)
  {
    if (other is null)
      return false;

    return card.Id == other.Id
      || card.Id == other.SourceId
      || card.SourceId > 0 && card.SourceId == other.Id
      || card.SourceId > 0 && card.SourceId == other.SourceId;
  }

  private static ActionDocument ProjectAction(GameAction action)
  {
    var cardAction = action as CardAction;
    return new ActionDocument(
      Read(() => action.ActionId),
      Read(() => action.Timestamp),
      Read(() => action.Type.ToString()),
      Read(() => action.Name),
      Read(() => action.IsDefault),
      Read(() => action.IsLocal),
      cardAction is null ? null : Read(() => cardAction.Card.Id),
      cardAction is null ? null : Read(() => cardAction.RequiresTargets)
    );
  }

  private static string ActionKey(GameAction action) =>
    $"{Read(() => action.Timestamp)}:{Read(() => action.ActionId)}:{Read(() => action.Name)}";

  private static T? Read<T>(Func<T> getter)
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

  public void Dispose()
  {
    GameProcessor.OnNewGame -= AttachGame;
    foreach (var game in _games.Values)
      game.ClearEvents();
    _games.Clear();
  }

  private sealed record GameDocument(
    DateTimeOffset CapturedAtUtc,
    int GameId,
    uint StateTimestamp,
    int Turn,
    string Phase,
    PromptDocument Prompt,
    IReadOnlyList<CardDocument> Cards
  );

  private sealed record PromptDocument(
    int Nonce,
    string Text,
    byte PromptedPlayer,
    IReadOnlyList<ActionDocument> Actions
  );

  private sealed record CardDocument(
    int Id,
    int SourceId,
    int Ctn,
    string Name,
    string? Zone,
    int? OwnerIndex,
    int? ControllerIndex,
    IReadOnlyList<ActionDocument> AvailableActions,
    IReadOnlyList<ActionDocument> PromptActions,
    IReadOnlyList<ActionDocument> LegalTargetFor
  );

  private sealed record ActionDocument(
    int ActionId,
    uint Timestamp,
    string? Type,
    string? Name,
    bool IsDefault,
    bool IsLocal,
    int? CardId,
    bool? RequiresTargets
  );
}
