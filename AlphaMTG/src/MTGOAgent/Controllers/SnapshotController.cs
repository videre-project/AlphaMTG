/** @file
  Copyright (c) 2026, Cory Bennett. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
**/

using Microsoft.AspNetCore.Mvc;


namespace AlphaMTG.MTGOAgent.Controllers;

/// <summary>
/// State reads: decision snapshot, WPF snapshot, card preview.
/// </summary>
[ApiController]
internal sealed class SnapshotController(Server server) : ControllerBase
{
  [HttpGet("/snapshot")]
  public IActionResult Snapshot([FromQuery] int? gameId = null)
  {
    try
    {
      int id = server.ResolveGameId(gameId);
      server.RefreshNow(id);
      return Ok(server.Snapshot(id));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpGet("/wpf-snapshot")]
  public IActionResult WpfSnapshot([FromQuery] int? gameId = null)
  {
    try
    {
      return Ok(server.GetWPFSnapshot(server.ResolveGameId(gameId)));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  [HttpGet("/preview-card")]
  public IActionResult PreviewCard(
    [FromQuery] int cardId, [FromQuery] int? gameId = null)
  {
    try
    {
      return Ok(server.PreviewCard(cardId, gameId));
    }
    catch (Exception exception)
    {
      return BadRequest(new { error = FirstLine(exception) });
    }
  }

  private static string FirstLine(Exception exception) =>
    exception.Message.Split('\n')[0];
}
