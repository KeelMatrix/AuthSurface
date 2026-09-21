using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthSurface.FixtureApp;

/// <summary>Controller endpoints used to expose attribute-based runtime metadata.</summary>
[ApiController]
[Route("controller")]
[Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")]
public sealed class FixtureController : ControllerBase
{
    /// <summary>Returns a protected controller response.</summary>
    /// <param name="id">The route identifier.</param>
    [HttpGet("protected/{id:int}")]
    public IActionResult Protected(int id) => Ok(id);

    /// <summary>Returns an explicitly anonymous controller response.</summary>
    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult Anonymous() => Ok();
}
