/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using Microsoft.AspNetCore.Mvc;

using AlphaMTG.MTGOAgent.Controllers.Models;
using AlphaMTG.MTGOAgent.UIA.Actions;


namespace AlphaMTG.MTGOAgent.Controllers;

/// <summary>
/// Game operations: action execution and selection.
/// </summary>
[ApiController]
internal sealed class GameController(Server server) : ControllerBase
{
  [HttpPost("/execute-action")]
  public IActionResult ExecuteAction([FromBody] ExecuteActionRequest request)
  {
    try
    {
      return Ok(server.ExecuteAction(
        request.ActionId,
        request.Nonce,
        request.GameId,
        request.TargetSetId,
        request.TargetId,
        request.ChoiceId,
        request.Number,
        request.Order,
        request.Distributions?
          .Select(entry => new DistributionInput(
            entry.TargetId, entry.Value))
          .ToList(),
        request.Wish));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpPost("/select-card")]
  public IActionResult SelectCard([FromBody] SelectCardRequest request)
  {
    try
    {
      return Ok(server.SelectCard(request.CardId, request.GameId));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpPost("/select-player")]
  public IActionResult SelectPlayer([FromBody] SelectPlayerRequest request)
  {
    try
    {
      return Ok(server.SelectPlayer(request.PlayerName, request.GameId));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpPost("/select-dialog")]
  public IActionResult SelectDialog([FromBody] SelectDialogRequest request)
  {
    try
    {
      return Ok(server.SelectDialog(request.CardName, request.GameId));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpPost("/click-control")]
  public IActionResult ClickControl([FromBody] ClickControlRequest request)
  {
    try
    {
      return Ok(server.ClickControl(request.Label, request.GameId));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  private static string FirstLine(Exception exception) =>
    exception.Message.Split('\n')[0];
}
