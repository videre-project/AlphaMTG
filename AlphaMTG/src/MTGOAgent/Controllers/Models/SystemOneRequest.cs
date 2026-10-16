/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json;


namespace AlphaMTG.MTGOAgent.Controllers.Models;

internal sealed record SystemOneRequest(
  string? Model,
  JsonElement State,
  Dictionary<string, SystemOneQuestionRequest>? Questions);

internal sealed record SystemOneQuestionRequest(
  string? Type,
  string? Instructions,
  JsonElement Criteria);
