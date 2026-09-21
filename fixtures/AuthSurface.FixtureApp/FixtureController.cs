using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthSurface.FixtureApp;

[ApiController]
[Route("controller")]
[Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")]
public sealed class FixtureController : ControllerBase
{
    [HttpGet("protected/{id:int}")]
    public IActionResult Protected(int id) => Ok(id);

    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult Anonymous() => Ok();
}
