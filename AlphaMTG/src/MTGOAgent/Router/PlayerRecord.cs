/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using System.IO;
using System.Text.Json;

using AlphaMTG.MTGOAgent.Router.DecisionModel;


namespace AlphaMTG.MTGOAgent.Router;

internal sealed record PlayerRecord(int Index, string Name, int Life);
