/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/


namespace AlphaMTG.MTGOAgent.Controllers.Models;

internal sealed record SelectCardRequest(
  int CardId,
  int? GameId = null
);
