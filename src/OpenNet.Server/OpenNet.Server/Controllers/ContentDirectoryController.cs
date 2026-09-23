using Microsoft.AspNetCore.Mvc;
using OpenNet.Server.Application;
using OpenNet.Server.Contracts;

namespace OpenNet.Server.Controllers;

[ApiController]
[Route("api/v1/content")]
public sealed class ContentDirectoryController(ContentDirectoryService directory)
    : ControllerBase
{
    [HttpPost("nodes/register")]
    [ProducesResponseType<RegisterContentNodeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegisterContentNodeResponse>> Register(
        [FromBody] RegisterContentNodeRequest request,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Connection.RemoteIpAddress is not { } remoteAddress)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "The server could not determine the request's remote address.");
        }

        try
        {
            RegisterContentNodeResponse response = await directory.RegisterAsync(
                request,
                remoteAddress,
                cancellationToken);
            return Ok(response);
        }
        catch (ContentDirectoryConflictException exception)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Content directory conflict",
                Detail = exception.Message
            });
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(exception.ParamName ?? string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpPost("nodes/{nodeId}/heartbeat")]
    [ProducesResponseType<ContentHeartbeatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContentHeartbeatResponse>> Heartbeat(
        string nodeId,
        [FromBody] ContentHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        ContentHeartbeatResponse? response = await directory.HeartbeatAsync(
            nodeId,
            request.LeaseId,
            cancellationToken);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet("lookup")]
    [ProducesResponseType<ContentLookupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContentLookupResponse>> Lookup(
        [FromQuery] int algorithm,
        [FromQuery] string digest,
        [FromQuery] int? maxPeers,
        [FromQuery] string? excludeNodeId,
        [FromQuery] bool prepare = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ContentLookupResponse? response = await directory.LookupAsync(
                algorithm,
                digest,
                maxPeers,
                excludeNodeId,
                prepare,
                cancellationToken);
            return response is null ? NotFound() : Ok(response);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(exception.ParamName ?? string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }
    }
    [HttpGet("nodes/{nodeId}/wakeups")]
    [ProducesResponseType<IReadOnlyList<ContentWakeupResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ContentWakeupResponse>>> PollWakeups(
        string nodeId,
        [FromQuery] Guid leaseId,
        [FromQuery] int? maxItems,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentWakeupResponse>? response =
            await directory.PollWakeupsAsync(
                nodeId,
                leaseId,
                maxItems,
                cancellationToken);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpPost("nodes/{nodeId}/wakeups/{wakeRequestId:guid}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteWakeup(
        string nodeId,
        Guid wakeRequestId,
        [FromBody] CompleteContentWakeupRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            bool completed = await directory.CompleteWakeupAsync(
                nodeId,
                wakeRequestId,
                request,
                cancellationToken);
            return completed ? NoContent() : NotFound();
        }
        catch (ContentDirectoryConflictException exception)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Canonical content conflict",
                Detail = exception.Message
            });
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(
                exception.ParamName ?? string.Empty,
                exception.Message);
            return ValidationProblem(ModelState);
        }
    }

    [HttpGet("manifests/{contentId:guid}")]
    [Produces("application/x-bittorrent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetManifest(
        Guid contentId,
        CancellationToken cancellationToken)
    {
        byte[]? manifest = await directory.GetCanonicalManifestAsync(
            contentId,
            cancellationToken);
        return manifest is null
            ? NotFound()
            : File(manifest, "application/x-bittorrent");
    }

}
