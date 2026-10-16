/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Router;

/// <summary>
/// Names the model, tokenizer, and tensors in a mounted weights directory.
/// Loading these names from router.json allows exports to change their tensor names.
/// </summary>
internal sealed record RouterConfig(
  string Model,
  string Tokenizer,
  RouterInputs Inputs,
  string Output,
  int MaxLength);
