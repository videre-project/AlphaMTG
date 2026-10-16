/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.Text.Json.Serialization;


namespace AlphaMTG.MTGOAgent.Controllers.Models;

internal sealed record SelectCardRequest(
  [property: JsonRequired] int CardId,
  [property: JsonRequired] int Nonce,
  int? GameId = null
);
