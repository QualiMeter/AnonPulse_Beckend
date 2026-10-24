using AnonPulse.Api.Contracts.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnonPulse.Api.Controllers;

[ApiController]
[Route("api/auth/csrf-token")]
public sealed class CsrfController(IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    [Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ActionResult<CsrfTokenResponse> Get()
    {
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new CsrfTokenResponse(
            tokens.RequestToken ?? throw new InvalidOperationException(
                "The antiforgery request token was not generated.")));
    }
}
