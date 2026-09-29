using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;

namespace Shoko.Plugin.Vouch.Tests;

/// <summary>
/// A stand-in for the host's shared authentication throttle.
/// </summary>
/// <remarks>
/// Written out rather than mocked because the tests care less about whether
/// the service was called than about <em>which bucket</em> the plugin wrote
/// to. The two lockouts are set by a test; the two ledgers record what the
/// plugin did with them.
/// </remarks>
internal sealed class FakeAuthenticationThrottleService : IAuthenticationThrottleService
{
    /// <inheritdoc/>
    public event EventHandler<AuthenticationFailedEventArgs>? AuthenticationFailed { add { } remove { } }

    /// <summary>What a test wants the client to be locked out for, if at all.</summary>
    public TimeSpan? ClientLockout { get; set; }

    /// <summary>What a test wants the user to be locked out for, if at all.</summary>
    public TimeSpan? UserLockout { get; set; }

    /// <summary>Failures registered against the client.</summary>
    public int ClientFailures { get; private set; }

    /// <summary>
    /// Failures registered against the user. Expected to stay at zero: a
    /// wrong pairing code is not a wrong password for the approver.
    /// </summary>
    public int UserFailures { get; private set; }

    /// <summary>Resets of the client.</summary>
    public int ClientResets { get; private set; }

    /// <summary>Resets of the user.</summary>
    public int UserResets { get; private set; }

    /// <summary>How often the plugin asked before acting.</summary>
    public int Checks { get; private set; }

    /// <summary>
    /// The username the plugin last asked about. Expected to be the
    /// approver's, never a pairing code.
    /// </summary>
    public string? LastUsernameChecked { get; private set; }

    public int MaxFailedAttempts => 10;

    public TimeSpan AttemptWindow => TimeSpan.FromMinutes(15);

    public TimeSpan InitialLockout => TimeSpan.FromMinutes(15);

    public TimeSpan MaxLockout => TimeSpan.FromDays(1);

    public StatusCodeResult? ThrottleAuthentication(HttpContext context, string username)
    {
        Checks++;
        LastUsernameChecked = username;

        var remaining = UserLockout is null
            ? ClientLockout
            : ClientLockout is null || UserLockout > ClientLockout
                ? UserLockout
                : ClientLockout;
        if (remaining is null)
            return null;

        context.Response.Headers.RetryAfter = ((int)remaining.Value.TotalSeconds).ToString();
        return new StatusCodeResult(StatusCodes.Status429TooManyRequests);
    }

    public TimeSpan? GetRemainingLockout(HttpContext context) => ClientLockout;

    public TimeSpan? GetRemainingLockout(HubCallerContext context) => ClientLockout;

    public TimeSpan? GetRemainingLockout(IUser user) => UserLockout;

    public void RegisterFailure(HttpContext context) => ClientFailures++;

    public void RegisterFailure(HubCallerContext context) => ClientFailures++;

    public void RegisterFailure(IUser user) => UserFailures++;

    public void Reset(HttpContext context) => ClientResets++;

    public void Reset(HubCallerContext context) => ClientResets++;

    public void Reset(IUser user) => UserResets++;
}
