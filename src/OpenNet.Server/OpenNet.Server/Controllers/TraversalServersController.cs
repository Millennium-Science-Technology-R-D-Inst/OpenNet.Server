using Microsoft.AspNetCore.Mvc;
using OpenNet.Server.Application;
using OpenNet.Server.Contracts;

namespace OpenNet.Server.Controllers;

[ApiController]
[Route("api/v1/traversal/servers")]
public sealed class TraversalServersController(TraversalDirectoryService directory)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TraversalServerListResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TraversalServerListResponse>> GetAvailable(
        CancellationToken cancellationToken)
    {
        TraversalServerListResponse response =
            await directory.GetAvailableAsync(cancellationToken);
        Response.Headers.CacheControl =
            $"public,max-age={response.CacheLifetimeSeconds}";
        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType<TraversalServerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TraversalServerResponse>> Upsert(
        [FromBody] UpsertTraversalServerRequest request,
        CancellationToken cancellationToken)
    {
        if (!directory.IsManagementRequestAuthorized(Request))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await directory.UpsertAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(exception.ParamName ?? string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("heartbeat")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Heartbeat(
        [FromBody] TraversalHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        if (!directory.IsManagementRequestAuthorized(Request))
        {
            return Unauthorized();
        }

        return await directory.HeartbeatAsync(request.ServerId, cancellationToken)
            ? NoContent()
            : NotFound();
    }
}
