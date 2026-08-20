using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shoko.Abstractions.Web.Attributes;

namespace Shoko.Plugin.Vouch.API.Controllers;

/// <summary>
/// Serves the approval page — the thing the QR code points at.
/// </summary>
/// <remarks>
/// <para>
/// A single embedded HTML file with no external resources, because the page
/// has to work on a phone on a home network with no internet, and because a
/// page that fetches script from elsewhere is a poor place to put a
/// confirmation that issues keys.
/// </para>
/// <para>
/// It is a fallback rather than the intended surface. A front-end that wants
/// this flow inside its own design calls the v1 API directly and never loads
/// this page; the page exists so the plugin is useful on its own, and so
/// that the verification URI in a QR code always leads somewhere.
/// </para>
/// <para>
/// Anonymous, because being signed in is the page's own business: it reads
/// the session Shoko's web UI stores on this origin, and offers the ordinary
/// sign-in form when there is not one. Everything it does afterwards goes
/// through the authenticated half of the API, which is where the gate
/// actually is.
/// </para>
/// </remarks>
[AllowAnonymous]
[DatabaseBlockedExempt]
[ApiController]
[Route(Constants.ApprovalPath)]
public class ApprovalPageController : ControllerBase
{
    private const string ResourceName = "Shoko.Plugin.Vouch.Assets.approve.html";

    /// <summary>
    /// Serves the approval page.
    /// </summary>
    /// <returns>The page.</returns>
    [HttpGet]
    public IActionResult GetPage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
            return StatusCode(500, "The approval page is missing from the plugin assembly.");

        using var reader = new StreamReader(stream);
        // No-store rather than merely no-cache: the URL carries a pairing
        // code, and a copy of the page sitting in a shared browser cache is
        // a copy of that code sitting somewhere nobody thought about.
        Response.Headers.CacheControl = "no-store";
        return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    }
}
