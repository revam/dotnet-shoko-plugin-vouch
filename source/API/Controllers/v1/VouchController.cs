using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.Web.Attributes;
using Shoko.Plugin.Vouch.API.Models;
using Shoko.Plugin.Vouch.Configuration;
using Shoko.Plugin.Vouch.Services;

namespace Shoko.Plugin.Vouch.API.Controllers.v1;

/// <summary>
/// The pairing handshake: a device asks, a signed-in person answers, and the
/// device that asked collects the key.
/// </summary>
/// <remarks>
/// <para>
/// Three endpoints face the device being paired and are anonymous, because
/// the device has no credential yet — that is the entire problem being
/// solved. Three face the person answering and require a signed-in session,
/// because that session is where the authority comes from. Possession of a
/// code is not authority, and no anonymous endpoint here can produce a key
/// without one of the authenticated ones having been called first.
/// </para>
/// <para>
/// The interaction is RFC 8628's — device code, short user code,
/// verification URI, and polling until approval, denial or expiry — without
/// any of its machinery. Nothing here is an OAuth endpoint, there is no
/// authorization server, and what is issued at the end is an ordinary Shoko
/// API key that shows up in the user's token list like any other.
/// </para>
/// </remarks>
/// <param name="userService">The user service, which mints and revokes the key.</param>
/// <param name="pairingStore">The pairing state machine.</param>
/// <param name="configurationProvider">The configuration provider.</param>
/// <param name="logger">The logger instance.</param>
[ApiController]
[Route("/api/plugin/Vouch/v1")]
public class VouchController(
    IUserService userService,
    PairingStore pairingStore,
    ConfigurationProvider<VouchPluginConfiguration> configurationProvider,
    ILogger<VouchController> logger) : ControllerBase
{
    private static readonly Version AssemblyVersion = Assembly.GetExecutingAssembly().GetName().Version!;

    private readonly IUserService _userService = userService;

    private readonly PairingStore _pairingStore = pairingStore;

    private readonly ConfigurationProvider<VouchPluginConfiguration> _configurationProvider = configurationProvider;

    private readonly ILogger<VouchController> _logger = logger;

    /// <summary>
    /// Checks whether the plugin is installed and describes the flow.
    /// </summary>
    /// <returns>The plugin version and the shape of a pairing request.</returns>
    [AllowAnonymous]
    [DatabaseBlockedExempt]
    [InitFriendly]
    [HttpGet("Available")]
    public ActionResult<AvailableResponse> Available()
        => Ok(new AvailableResponse
        {
            Version = AssemblyVersion.ToString(3),
            VerificationPath = Constants.ApprovalPath,
            UserCodeLength = PairingStore.UserCodeLength,
            RequestLifetimeSeconds = (int)PairingStore.RequestLifetime.TotalSeconds,
            PollIntervalSeconds = (int)PairingStore.PollInterval.TotalSeconds,
        });

    // ──────────────────────────────────────────────
    //  The device being paired
    // ──────────────────────────────────────────────

    /// <summary>
    /// Opens a pairing request for a device that has no credential.
    /// </summary>
    /// <remarks>
    /// What comes back is a request, not a credential. The public half is
    /// meant to be displayed and photographed by a camera in the room; it
    /// names this request and can do nothing else. The secret half is what
    /// makes the caller the device that asked, and it must never be
    /// rendered.
    /// </remarks>
    /// <param name="request">The device's name and, optionally, its kind.</param>
    /// <returns>Both halves of the new request, and where to send a phone.</returns>
    [AllowAnonymous]
    [HttpPost("Request")]
    public ActionResult<RequestPairingResponse> RequestPairing([FromBody] RequestPairingRequest request)
    {
        if (!TryGetClientIps(out var ips))
            return NoClientIp("Pairing request");

        if (PairingStore.NormalizeDeviceName(request.DeviceName) is not { } deviceName)
            return StatusCode(400, new VouchResponse
            {
                Message = "A device name of 1-64 printable characters is required.",
            });

        if (!_pairingStore.CanCreate(ips, out var nextAllowedAt))
        {
            _logger.LogWarning("Pairing request denied due to rate limiting. Client IPs: {IPs}", ips);
            return RateLimited("Too many pairing requests. Please try again later.", nextAllowedAt);
        }

        var deviceType = PairingStore.NormalizeDescriptor(request.DeviceType, PairingStore.DeviceTypeLimit);
        var userAgent = PairingStore.NormalizeDescriptor(Request.Headers.UserAgent.FirstOrDefault(), PairingStore.UserAgentLimit);
        var pairing = _pairingStore.Create(deviceName, deviceType, userAgent, ips);
        var verificationUri = BuildVerificationUri();

        return Ok(new RequestPairingResponse
        {
            DeviceCode = pairing.DeviceCode,
            UserCode = pairing.UserCode,
            VerificationUri = verificationUri,
            VerificationUriComplete = $"{verificationUri}?code={Uri.EscapeDataString(pairing.UserCode)}",
            ExpiresAt = pairing.ExpiresAt,
            ExpiresIn = (int)PairingStore.RequestLifetime.TotalSeconds,
            Interval = (int)pairing.Interval.TotalSeconds,
        });
    }

    /// <summary>
    /// Asks after a pairing request, and collects the key once there is one.
    /// </summary>
    /// <remarks>
    /// The one place in this API where a key is ever returned, and it is
    /// returned to the device that opened the request, over a call only that
    /// device can make. It is returned exactly once: a second poll gets
    /// <c>completed</c> and nothing else, so a request cannot be replayed.
    /// </remarks>
    /// <param name="request">The secret half of the request.</param>
    /// <returns>The status, and the key on the one poll that finds it.</returns>
    [AllowAnonymous]
    [HttpPost("Poll")]
    public ActionResult<PollPairingResponse> Poll([FromBody] PollPairingRequest request)
    {
        var result = _pairingStore.Poll(request.DeviceCode);
        var response = new PollPairingResponse
        {
            Status = ToWireStatus(result.Status),
            ApiKey = result.ApiKey,
            Username = result.Username,
            DeviceName = result.DeviceName,
            ApiKeyExpiresAt = result.ApiKeyExpiresAt,
            ExpiresAt = result.ExpiresAt,
            Interval = (int)result.Interval.TotalSeconds,
            SlowDown = result.SlowDown,
            Message = Describe(result.Status, result.SlowDown),
        };

        // A device polling faster than it was told to is asked to back off
        // with the same 429 and Retry-After every other limit here uses. The
        // body still carries the status, so a client that ignores the header
        // is not left guessing.
        if (result.SlowDown)
        {
            Response.Headers.RetryAfter = ((int)result.Interval.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(429, response);
        }

        // Unknown is a 404 rather than a 200 carrying "unknown", because a
        // client with a device code from a previous run of the server should
        // find out plainly rather than by reading a field.
        if (result.Status is PairingStatus.Unknown)
            return StatusCode(404, response);

        return Ok(response);
    }

    // ──────────────────────────────────────────────
    //  The device doing the vouching
    // ──────────────────────────────────────────────

    /// <summary>
    /// Describes what is asking, for the confirmation screen to show.
    /// </summary>
    /// <remarks>
    /// Requires a signed-in session — this is the half of the flow that
    /// carries the authority, and a code alone must not open a window onto
    /// anything.
    /// </remarks>
    /// <param name="userCode">The public code, in any spacing or casing.</param>
    /// <returns>The pending request, or why there is not one.</returns>
    [Authorize]
    [HttpGet("Pending/{userCode}")]
    public ActionResult<PendingPairingResponse> Pending([FromRoute] string userCode)
    {
        if (!TryGetClientIps(out var ips))
            return NoClientIp("Pairing lookup");

        if (GetCurrentUser() is not { } user)
            return StatusCode(401, new VouchResponse { Message = "Sign in to answer a pairing request." });

        if (!_pairingStore.CanLookup(ips, out var nextAllowedAt))
        {
            _logger.LogWarning("Pairing lookup denied due to rate limiting. Client IPs: {IPs}", ips);
            return RateLimited("Too many attempts. Please try again later.", nextAllowedAt);
        }

        var status = _pairingStore.Lookup(userCode, ips, out var pending);
        if (status is not PairingStatus.Pending || pending is null)
            return StatusCode(status is PairingStatus.Unknown ? 404 : 409, new AnswerPairingResponse
            {
                Status = ToWireStatus(status),
                Message = Describe(status, false),
            });

        return Ok(new PendingPairingResponse
        {
            UserCode = pending.UserCode,
            DeviceName = pending.DeviceName,
            DeviceType = pending.DeviceType,
            UserAgent = pending.UserAgent,
            RequestedFrom = pending.RequestedFrom,
            RequestedAt = pending.RequestedAt,
            ExpiresAt = pending.ExpiresAt,
            DeviceNameInUse = HasKeyNamed(user, pending.DeviceName),
        });
    }

    /// <summary>
    /// Approves a pairing request, minting a key for the signed-in user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authority is the caller's session, and the key belongs to the
    /// caller. Possession of the code gets someone as far as this endpoint
    /// and no further.
    /// </para>
    /// <para>
    /// The device name on the key is the one stored with the request — the
    /// one the approver was shown — and never one supplied here, so what is
    /// approved is what was displayed.
    /// </para>
    /// </remarks>
    /// <param name="request">The public code being approved.</param>
    /// <returns>What happened. Never a key.</returns>
    [Authorize]
    [HttpPost("Approve")]
    public async Task<ActionResult<AnswerPairingResponse>> Approve([FromBody] AnswerPairingRequest request)
    {
        if (!TryGetClientIps(out var ips))
            return NoClientIp("Pairing approval");

        if (GetCurrentUser() is not { } user)
            return StatusCode(401, new VouchResponse { Message = "Sign in to answer a pairing request." });

        if (!_pairingStore.CanLookup(ips, out var nextAllowedAt))
        {
            _logger.LogWarning("Pairing approval denied due to rate limiting. Client IPs: {IPs}", ips);
            return RateLimited("Too many attempts. Please try again later.", nextAllowedAt);
        }

        var status = _pairingStore.BeginApproval(request.UserCode, ips, out var ticket);
        if (status is not PairingStatus.Pending || ticket is null)
            return StatusCode(status is PairingStatus.Unknown ? 404 : 409, new AnswerPairingResponse
            {
                Status = ToWireStatus(status),
                Message = Describe(status, false),
            });

        var deviceName = ticket.Request.DeviceName;
        var lifetimeHours = _configurationProvider.Load().IssuedKeyLifetimeHours;
        ApiToken token;
        try
        {
            token = lifetimeHours is { } hours and > 0
                ? await _userService.GenerateApiTokenForUser(user, deviceName, DateTime.Now.AddHours(hours))
                : await _userService.GenerateApiTokenForUser(user, deviceName);
        }
        catch (Exception ex)
        {
            // The claim goes back, so the request is answerable again rather
            // than stuck in a state only the sweep can clear.
            _pairingStore.AbandonApproval(ticket);
            _logger.LogError(ex, "Failed to mint a key while approving a pairing request for {DeviceName}.", deviceName);
            return StatusCode(500, new VouchResponse { Message = "Could not issue a key for that device." });
        }

        _pairingStore.CompleteApproval(ticket, token.Token, user.Username, token.ExpiresAt);

        return Ok(new AnswerPairingResponse
        {
            Status = ToWireStatus(PairingStatus.Approved),
            DeviceName = deviceName,
            Username = user.Username,
            Message = $"{deviceName} has been signed in as {user.Username}.",
        });
    }

    /// <summary>
    /// Refuses a pairing request.
    /// </summary>
    /// <remarks>
    /// Not an error path. The requesting device is told plainly that someone
    /// said no, so it can fall back to the ordinary sign-in screen it would
    /// have shown had nobody tried to pair it — a refused pairing leaves
    /// everyone exactly where they started.
    /// </remarks>
    /// <param name="request">The public code being refused.</param>
    /// <returns>What happened.</returns>
    [Authorize]
    [HttpPost("Deny")]
    public ActionResult<AnswerPairingResponse> Deny([FromBody] AnswerPairingRequest request)
    {
        if (!TryGetClientIps(out var ips))
            return NoClientIp("Pairing refusal");

        if (GetCurrentUser() is not { } user)
            return StatusCode(401, new VouchResponse { Message = "Sign in to answer a pairing request." });

        if (!_pairingStore.CanLookup(ips, out var nextAllowedAt))
        {
            _logger.LogWarning("Pairing refusal denied due to rate limiting. Client IPs: {IPs}", ips);
            return RateLimited("Too many attempts. Please try again later.", nextAllowedAt);
        }

        var status = _pairingStore.Deny(request.UserCode, ips, user.Username);
        var response = new AnswerPairingResponse
        {
            Status = ToWireStatus(status),
            Username = user.Username,
            Message = Describe(status, false),
        };

        if (status is PairingStatus.Denied)
            return Ok(response);

        return StatusCode(status is PairingStatus.Unknown ? 404 : 409, response);
    }

    // ──────────────────────────────────────────────
    //  Shared
    // ──────────────────────────────────────────────

    private IUser? GetCurrentUser()
        => _userService.GetUserFromHttpContext(HttpContext);

    private bool HasKeyNamed(IUser user, string deviceName)
        => _userService.GetApiTokensForUser(user)
            .Any(token => token.ExpiresAt is null && token.Device.Trim().Equals(deviceName, StringComparison.OrdinalIgnoreCase));

    private static string ToWireStatus(PairingStatus status)
        => status.ToString().ToLowerInvariant();

    private static string Describe(PairingStatus status, bool slowDown)
        => status switch
        {
            PairingStatus.Pending when slowDown => "Polling too often. Wait for the interval given before asking again.",
            PairingStatus.Pending => "Waiting for someone to confirm on a device that is already signed in.",
            PairingStatus.Approved => "Approved.",
            PairingStatus.Denied => "Someone refused this request. Sign in the usual way instead.",
            PairingStatus.Expired => "This code took too long to be confirmed. Ask for a new one.",
            PairingStatus.Completed => "This code has already been used. Ask for a new one.",
            _ => "No such code. Ask for a new one.",
        };

    private ActionResult NoClientIp(string what)
    {
        _logger.LogError("{What} denied — unable to determine client IP address", what);
        return StatusCode(400, new VouchResponse { Message = "Unable to determine client IP address." });
    }

    private ActionResult RateLimited(string message, DateTimeOffset? nextAllowedAt)
    {
        var response = new VouchResponse { Message = message };
        if (nextAllowedAt.HasValue)
        {
            response.RetryAfter = nextAllowedAt.Value;
            var seconds = (int)(nextAllowedAt.Value - DateTimeOffset.UtcNow).TotalSeconds;
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return StatusCode(429, response);
    }

    /// <summary>
    /// The address to show the approver, and the key to rate-limit by.
    /// </summary>
    /// <remarks>
    /// The same rule as <c>Forgotten</c> uses: the direct connection is
    /// always what is counted, and a forwarded chain is only believed when
    /// the server has been told it sits behind a proxy.
    /// </remarks>
    private bool TryGetClientIps([NotNullWhen(true)] out string? ips)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (remoteIp is not { Length: > 0 })
        {
            ips = null;
            return false;
        }

        var forwardedFor = _configurationProvider.Load().TrustProxy
            ? Request.Headers["X-Forwarded-For"].FirstOrDefault()
            : null;
        ips = string.IsNullOrEmpty(forwardedFor) ? remoteIp : $"{forwardedFor} | direct: {remoteIp}";
        return true;
    }

    /// <summary>
    /// Where to send the phone.
    /// </summary>
    /// <remarks>
    /// Built from the request the device itself just made, which is by
    /// definition an address that reaches this server from where the device
    /// is standing. Forwarded headers are honoured only when the server has
    /// been told it is behind a proxy — otherwise any caller could choose
    /// the host that ends up inside a QR code.
    /// </remarks>
    private string BuildVerificationUri()
    {
        var scheme = Request.Scheme;
        var host = Request.Host.Value;
        if (_configurationProvider.Load().TrustProxy)
        {
            if (Request.Headers["X-Forwarded-Proto"].FirstOrDefault() is { Length: > 0 } forwardedProto)
                scheme = forwardedProto.Split(',')[0].Trim();
            if (Request.Headers["X-Forwarded-Host"].FirstOrDefault() is { Length: > 0 } forwardedHost)
                host = forwardedHost.Split(',')[0].Trim();
        }

        return $"{scheme}://{host}{Constants.ApprovalPath}";
    }
}
