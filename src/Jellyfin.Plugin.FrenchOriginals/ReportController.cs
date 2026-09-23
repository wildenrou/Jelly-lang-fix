using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FrenchOriginals;

[ApiController]
[Route("FrenchOriginals")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ReportController(StateStore state) : ControllerBase
{
    [HttpGet("Report")]
    public ContentResult Report() => Content(state.ReadReport(), "application/json");
}
