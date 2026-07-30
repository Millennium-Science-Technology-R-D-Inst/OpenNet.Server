using Microsoft.AspNetCore.Mvc;
using OpenNet.Server.Application;
using OpenNet.Server.Contracts;

namespace OpenNet.Server.Controllers;

[ApiController]
[Route("api/v1/update")]
public sealed class UpdatesController(UpdateCatalogService catalog)
    : ControllerBase
{
    [HttpGet("latest")]
    [ProducesResponseType<UpdatePackageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UpdatePackageResponse> GetLatest(
        [FromQuery] string channel = "stable",
        [FromQuery] string architecture = "x64",
        [FromQuery] string packageType = "installer")
    {
        UpdatePackageResponse? response =
            catalog.GetLatest(channel, architecture, packageType);
        if (response is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No matching update package",
                Detail =
                    $"No active {channel}/{architecture}/{packageType} package is configured.",
                Status = StatusCodes.Status404NotFound
            });
        }

        Response.Headers.CacheControl =
            $"public,max-age={catalog.CacheDurationSeconds}";
        return Ok(response);
    }
}
