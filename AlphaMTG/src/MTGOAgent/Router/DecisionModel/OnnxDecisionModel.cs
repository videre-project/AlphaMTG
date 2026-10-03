/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using AlphaMTG.MTGOAgent.Router.Tokenizer;


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// An <see cref="IDecisionModel"/> backed by a span-extractor ONNX export:
/// one forward pass per question over its instructions, criteria, and the
/// shared state. The input layout is the traced span-extractor protocol.
/// </summary>
internal sealed class OnnxDecisionModel : IDecisionModel, IDisposable
{
  private readonly InferenceSession _session;
  private readonly ITokenizer _tokenizer;
  private readonly RouterInputs _inputs;
  private readonly string _output;
  private readonly int _maxLength;
  private bool _disposed;

  public OnnxDecisionModel(string weightsDirectory)
  {
    RouterConfig config = ReadConfig(weightsDirectory);
    _inputs = config.Inputs;
    _output = config.Output;
    _maxLength = config.MaxLength;
    _session = new InferenceSession(
      Path.Combine(weightsDirectory, config.Model));
    _tokenizer = new UnigramTokenizer(
      Path.Combine(weightsDirectory, config.Tokenizer));
  }

  public IReadOnlyDictionary<string, DecisionAnswer> Decide(
    string state, IReadOnlyDictionary<string, DecisionQuestion> questions)
  {
    if (_disposed)
      throw new ObjectDisposedException(nameof(OnnxDecisionModel));
    var answers = new Dictionary<string, DecisionAnswer>(questions.Count);
    foreach ((string name, DecisionQuestion question) in questions)
    {
      IReadOnlyList<ScoredCandidate> scored = ScoreQuestion(state, question);
      answers.Add(name, ToAnswer(question, scored));
    }
    return answers;
  }

  private IReadOnlyList<ScoredCandidate> ScoreQuestion(
    string state, DecisionQuestion question)
  {
    IReadOnlyList<DecisionCandidate> candidates = question.Criteria;
    GlinerInputBuilder.BuiltInput input = GlinerInputBuilder.Build(
      _tokenizer, question.Instructions, state, candidates, _maxLength);
    var idTensor = new DenseTensor<long>(
      input.Ids, new[] { 1, input.Ids.Length });
    var maskTensor = new DenseTensor<long>(
      input.Mask, new[] { 1, input.Mask.Length });
    var positionTensor = new DenseTensor<long>(
      input.LabelPositions, new[] { input.LabelPositions.Length });
    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
      _session.Run(new List<NamedOnnxValue>
      {
        NamedOnnxValue.CreateFromTensor(_inputs.Ids, idTensor),
        NamedOnnxValue.CreateFromTensor(_inputs.Mask, maskTensor),
        NamedOnnxValue.CreateFromTensor(_inputs.Positions, positionTensor),
      });
    float[] logits = results
      .First(value => value.Name == _output)
      .AsEnumerable<float>()
      .ToArray();
    if (logits.Length != candidates.Count)
      throw new InvalidOperationException(
        $"Model returned {logits.Length} logits for {candidates.Count} criteria.");
    var scored = new List<ScoredCandidate>(candidates.Count);
    for (int i = 0; i < input.LabelIds.Count; i++)
      scored.Add(new ScoredCandidate(input.LabelIds[i], logits[i]));
    return scored;
  }

  private static DecisionAnswer ToAnswer(
    DecisionQuestion question, IReadOnlyList<ScoredCandidate> scored)
  {
    if (scored.Count < 2 || scored.Any(item => !float.IsFinite(item.Score)))
      throw new InvalidOperationException("The model returned invalid decision logits.");

    // Stable softmax: only differences between logits matter.
    double maximum = scored.Max(item => (double)item.Score);
    double[] weights = scored
      .Select(item => Math.Exp(item.Score - maximum))
      .ToArray();
    double total = weights.Sum();
    var probabilities = new Dictionary<string, double>(scored.Count);
    for (int i = 0; i < scored.Count; i++)
      probabilities.Add(scored[i].Id, weights[i] / total);

    double[] ranked = probabilities.Values.OrderByDescending(value => value).ToArray();
    double confidence = ranked[0] - ranked[1];
    if (question.Type == "noul")
    {
      double yes = probabilities["true"];
      return new DecisionAnswer("noul", Math.Abs(2 * yes - 1), Noul: yes);
    }
    if (question.Type == "choice")
    {
      string choice = probabilities.MaxBy(pair => pair.Value).Key;
      return new DecisionAnswer("choice", confidence,
        Choice: choice, Probabilities: probabilities);
    }
    if (question.Type == "score")
    {
      int score = int.Parse(probabilities.MaxBy(pair => pair.Value).Key);
      double expected = probabilities.Sum(pair =>
        int.Parse(pair.Key) * pair.Value);
      var legend = question.Criteria.ToDictionary(
        item => item.Id, item => item.Description ?? item.Label);
      return new DecisionAnswer("score", confidence,
        Score: score, ExpectedLevel: expected,
        Probabilities: probabilities, Legend: legend);
    }
    throw new ArgumentException($"Unsupported question type '{question.Type}'.");
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    _session.Dispose();
  }

  private static RouterConfig ReadConfig(string weightsDirectory)
  {
    string path = Path.Combine(weightsDirectory, "router.json");
    if (!File.Exists(path))
    {
      throw new InvalidOperationException(
        $"Router weights directory '{weightsDirectory}' has no router.json.");
    }
    RouterConfig? config = JsonSerializer.Deserialize<RouterConfig>(
      File.ReadAllText(path),
      new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    return config
      ?? throw new InvalidOperationException(
        $"Router config '{path}' did not parse.");
  }
}
