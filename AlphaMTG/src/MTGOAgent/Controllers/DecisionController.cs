/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

using AlphaMTG.MTGOAgent.Controllers.Models;
using AlphaMTG.MTGOAgent.Router.DecisionModel;


namespace AlphaMTG.MTGOAgent.Controllers;

/// <summary>
/// Runs decision questions using the mounted gliner-decide weights.
/// Each question receives its own instructions and criteria against one state.
/// </summary>
[ApiController]
[Route("")]
public sealed class DecisionController(IDecisionModel? model = null) : ControllerBase
{
  [HttpPost("v1/systemone")]
  public IActionResult Decide([FromBody] SystemOneRequest? request)
  {
    try
    {
      if (request is null)
        throw new ArgumentException("A request body is required.");
      if (request.Model != "gliner-decide")
        throw new ArgumentException("model must be 'gliner-decide'.");
      string state = RenderState(request.State);
      IReadOnlyDictionary<string, DecisionQuestion> questions =
        ParseQuestions(request.Questions, ActionLabels(request.State));
      if (model is null)
        return StatusCode(503, new
        {
          error = "No decision model configured: set Plugins:alphamtg:WeightsDirectory to a "
            + "weights directory containing router.json."
        });
      IReadOnlyDictionary<string, DecisionAnswer> answers = model.Decide(state, questions);
      return Ok(new { model = request.Model, answers });
    }
    catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "maxLength")
    {
      return UnprocessableEntity(new { error = exception.Message.Split('\n')[0] });
    }
    catch (ArgumentException exception)
    {
      return BadRequest(new { error = exception.Message });
    }
  }

  private static string RenderState(JsonElement state)
  {
    string rendered = state.ValueKind switch
    {
      JsonValueKind.String => state.GetString()!,
      JsonValueKind.Object when state.EnumerateObject().Any() => state.GetRawText(),
      JsonValueKind.Array when state.EnumerateArray().Any() => state.GetRawText(),
      JsonValueKind.Object or JsonValueKind.Array => string.Empty,
      _ => throw new ArgumentException("state must be a string, object, or array."),
    };
    if (string.IsNullOrWhiteSpace(rendered))
      throw new ArgumentException("state must be nonempty.");
    return rendered;
  }

  private static IReadOnlyDictionary<string, DecisionQuestion> ParseQuestions(
    Dictionary<string, SystemOneQuestionRequest>? requests,
    IReadOnlyDictionary<string, string> actionLabels)
  {
    if (requests is null || requests.Count == 0)
      throw new ArgumentException("questions must contain at least one named question.");

    var questions = new Dictionary<string, DecisionQuestion>(requests.Count);
    foreach ((string name, SystemOneQuestionRequest request) in requests)
    {
      if (string.IsNullOrWhiteSpace(name))
        throw new ArgumentException("Question names must be nonempty.");
      if (request is null || string.IsNullOrWhiteSpace(request.Instructions))
        throw new ArgumentException($"Question '{name}' needs instructions.");

      IReadOnlyList<DecisionCandidate> criteria = request.Type switch
      {
        "noul" => ParseNoul(request.Criteria),
        "choice" => ParseChoice(request.Criteria, actionLabels),
        "score" => ParseScore(request.Criteria),
        _ => throw new ArgumentException(
          $"Question '{name}' has unsupported type '{request.Type}'."),
      };
      questions.Add(name, new DecisionQuestion(
        request.Type!, request.Instructions, criteria));
    }
    return questions;
  }

  private static IReadOnlyList<DecisionCandidate> ParseNoul(JsonElement criteria)
  {
    if (criteria.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
      return new[]
      {
        new DecisionCandidate("true", "true", "Yes"),
        new DecisionCandidate("false", "false", "No"),
      };
    if (criteria.ValueKind != JsonValueKind.Object
      || criteria.EnumerateObject().Count() != 2
      || !criteria.TryGetProperty("true", out JsonElement yes)
      || !criteria.TryGetProperty("false", out JsonElement no))
      throw new ArgumentException(
        "noul criteria must have exactly 'true' and 'false' descriptions.");
    return new[]
    {
      new DecisionCandidate("true", "true", Description(yes)),
      new DecisionCandidate("false", "false", Description(no)),
    };
  }

  private static IReadOnlyList<DecisionCandidate> ParseChoice(
    JsonElement criteria, IReadOnlyDictionary<string, string> actionLabels)
  {
    if (criteria.ValueKind != JsonValueKind.Object)
      throw new ArgumentException("choice criteria must be an object.");
    var candidates = new List<DecisionCandidate>();
    foreach (JsonProperty option in criteria.EnumerateObject())
    {
      if (string.IsNullOrWhiteSpace(option.Name))
        throw new ArgumentException("choice option names must be nonempty.");
      candidates.Add(new DecisionCandidate(
        option.Name,
        actionLabels.GetValueOrDefault(option.Name) ?? option.Name,
        Description(option.Value)));
    }
    ValidateCount(candidates.Count);
    if (candidates.Select(item => item.Id).Distinct().Count() != candidates.Count)
      throw new ArgumentException("choice option names must be unique.");
    return candidates;
  }

  // Action IDs identify choices in one SDK update. Give the model a label
  // describing each source while retaining the submitted ID in the answer.
  private static IReadOnlyDictionary<string, string> ActionLabels(JsonElement state)
  {
    var labels = new Dictionary<string, string>();
    if (state.ValueKind != JsonValueKind.Object
      || !TryProperty(state, "actions", out JsonElement actions)
      || actions.ValueKind != JsonValueKind.Array)
      return labels;

    foreach (JsonElement action in actions.EnumerateArray())
    {
      if (action.ValueKind != JsonValueKind.Object
        || !TryProperty(action, "id", out JsonElement id)
        || id.ValueKind != JsonValueKind.Number
        || !id.TryGetInt32(out int actionId))
        continue;

      string key = actionId.ToString(CultureInfo.InvariantCulture);
      string source = "action";
      if (TryProperty(action, "source", out JsonElement actionSource)
        && actionSource.ValueKind == JsonValueKind.Object
        && TryProperty(actionSource, "cardId", out JsonElement cardId)
        && cardId.ValueKind == JsonValueKind.Number
        && cardId.TryGetInt32(out int card))
      {
        source = $"card:{card}";
      }
      else if (TryProperty(action, "name", out JsonElement name)
        && name.ValueKind == JsonValueKind.String)
      {
        string slug = Regex.Replace(
          name.GetString()!.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        bool control = TryProperty(action, "source", out JsonElement origin)
          && origin.ValueKind == JsonValueKind.Object
          && TryProperty(origin, "type", out JsonElement originType)
          && originType.ValueKind == JsonValueKind.String
          && originType.GetString() == "control";
        source = $"{(control ? "control" : "prompt")}:{(slug.Length > 0 ? slug : "action")}";
      }

      if (!labels.TryAdd(key, $"{source}/action:{key}"))
        throw new ArgumentException($"state.actions has duplicate action ID {key}.");
    }
    return labels;
  }

  private static bool TryProperty(
    JsonElement value, string name, out JsonElement property)
  {
    foreach (JsonProperty item in value.EnumerateObject())
    {
      if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        property = item.Value;
        return true;
      }
    }
    property = default;
    return false;
  }

  private static IReadOnlyList<DecisionCandidate> ParseScore(JsonElement criteria)
  {
    var candidates = new List<DecisionCandidate>();
    if (criteria.ValueKind == JsonValueKind.Array)
    {
      foreach (JsonElement level in criteria.EnumerateArray())
        AddLevel(candidates, Description(level));
    }
    else if (criteria.ValueKind == JsonValueKind.Object)
    {
      var levels = new List<(int Key, string Description)>();
      foreach (JsonProperty level in criteria.EnumerateObject())
      {
        if (!int.TryParse(level.Name, NumberStyles.Integer,
          CultureInfo.InvariantCulture, out int key))
          throw new ArgumentException(
            "score object criteria must have numeric keys.");
        levels.Add((key, Description(level.Value)));
      }
      if (levels.Select(level => level.Key).Distinct().Count() != levels.Count)
        throw new ArgumentException("score level keys must be unique.");
      foreach (var level in levels.OrderBy(level => level.Key))
        AddLevel(candidates, level.Description);
    }
    else
      throw new ArgumentException(
        "score criteria must be an ordered array or numeric-keyed object.");
    ValidateCount(candidates.Count);
    return candidates;
  }

  private static void AddLevel(List<DecisionCandidate> candidates,
    string description)
  {
    string id = candidates.Count.ToString(CultureInfo.InvariantCulture);
    candidates.Add(new DecisionCandidate(id, id, description));
  }

  private static string Description(JsonElement value)
  {
    if (value.ValueKind != JsonValueKind.String
      || string.IsNullOrWhiteSpace(value.GetString()))
      throw new ArgumentException("Every criterion needs a nonempty description.");
    return value.GetString()!;
  }

  private static void ValidateCount(int count)
  {
    if (count is < 2 or > 255)
      throw new ArgumentException("choice and score require 2 to 255 criteria.");
  }
}
