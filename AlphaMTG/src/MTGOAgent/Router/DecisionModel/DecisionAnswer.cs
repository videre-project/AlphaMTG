/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json.Serialization;


namespace AlphaMTG.MTGOAgent.Router.DecisionModel;

/// <summary>
/// A typed answer. Fields used only by another primitive are omitted on the wire.
/// Probabilities are softmax values from the local ONNX logits, not calibrated
/// probabilities from Fastino's hosted GLiDE service.
/// </summary>
internal sealed record DecisionAnswer(
  string Type,
  double Confidence,
  double? Noul = null,
  string? Choice = null,
  int? Score = null,
  [property: JsonPropertyName("expected_level")]
  double? ExpectedLevel = null,
  IReadOnlyDictionary<string, double>? Probabilities = null,
  IReadOnlyDictionary<string, string>? Legend = null);
