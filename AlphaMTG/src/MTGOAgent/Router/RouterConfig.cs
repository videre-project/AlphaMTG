/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Router;

/// <summary>
/// Weights-directory contract. The directory is mounted read-only and
/// replaced wholesale when you fine-tune: drop a new directory, point
/// <c>ALPHAMTG_ROUTER_WEIGHTS</c> at it, restart. Tensor names come from
/// here so a re-export with different names stays working.
/// </summary>
internal sealed record RouterConfig(
  string Model,
  string Tokenizer,
  RouterInputs Inputs,
  string Output,
  int MaxLength);
