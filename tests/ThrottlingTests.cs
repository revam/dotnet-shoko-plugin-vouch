using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Services;
using Shoko.Plugin.Vouch.API.Controllers.v1;
using Shoko.Plugin.Vouch.API.Models;
using Shoko.Plugin.Vouch.Configuration;
using Shoko.Plugin.Vouch.Services;
using Xunit;

namespace Shoko.Plugin.Vouch.Tests;

/// <summary>
/// Which bucket a pairing attempt is counted in, and who is shut out when
/// one fills up.
/// </summary>
/// <remarks>
/// <para>
/// The guessing counter is the host's, shared with the core sign-in and with
/// every other plugin, so the properties worth pinning down are the ones a
/// shared store makes easy to get wrong: that a wrong code is charged to the
/// client and never to the approver's account, that a code the caller was
/// legitimately given is charged to nobody, and that the username the host is
/// asked about is the approver's rather than the code — a code in the
/// username slot would land among the usernames, where one that read like a
/// real account would share that account's lockout.
/// </para>
/// <para>
/// Polling is deliberately absent from all of it, and one test says so.
/// </para>
/// </remarks>
public class ThrottlingTests
{
    private const string ClientIp = "10.0.0.9";

    private const string Approver = "alice";

    // ──────────────────────────────────────────────
    //  A code that matched nothing
    // ──────────────────────────────────────────────

    [Fact]
    public void A_code_that_matched_nothing_is_charged_to_the_client()
    {
        using var harness = new Harness();

        var result = harness.Controller.Pending("BCDF-GHJK");

        Assert.Equal(404, StatusOf(result));
        Assert.Equal(1, harness.Throttle.ClientFailures);
    }

    [Fact]
    public void A_code_that_matched_nothing_is_never_charged_to_the_approver()
    {
        using var harness = new Harness();

        for (var i = 0; i < 20; i++)
            harness.Controller.Pending("BCDF-GHJK");

        // The approver's own credential was never in question. Charging the
        // account would let anyone holding a session lock it out of signing
        // in, which is a denial of service dressed as a security control.
        Assert.Equal(20, harness.Throttle.ClientFailures);
        Assert.Equal(0, harness.Throttle.UserFailures);
    }

    [Fact]
    public void Refusing_a_code_that_matched_nothing_is_charged_to_the_client()
    {
        using var harness = new Harness();

        var result = harness.Controller.Deny(new AnswerPairingRequest { UserCode = "BCDF-GHJK" });

        Assert.Equal(404, StatusOf(result));
        Assert.Equal(1, harness.Throttle.ClientFailures);
        Assert.Equal(0, harness.Throttle.UserFailures);
    }

    [Fact]
    public async Task Approving_a_code_that_matched_nothing_is_charged_to_the_client()
    {
        using var harness = new Harness();

        var result = await harness.Controller.Approve(new AnswerPairingRequest { UserCode = "BCDF-GHJK" });

        Assert.Equal(404, StatusOf(result));
        Assert.Equal(1, harness.Throttle.ClientFailures);
        Assert.Equal(0, harness.Throttle.UserFailures);
    }

    [Fact]
    public void The_host_is_asked_about_the_approver_and_never_about_the_code()
    {
        using var harness = new Harness();

        harness.Controller.Pending("BCDF-GHJK");

        Assert.Equal(1, harness.Throttle.Checks);
        Assert.Equal(Approver, harness.Throttle.LastUsernameChecked);
    }

    // ──────────────────────────────────────────────
    //  A code the caller was given
    // ──────────────────────────────────────────────

    [Fact]
    public void A_real_code_is_charged_to_nobody()
    {
        using var harness = new Harness();
        var pairing = harness.OpenRequest();

        var result = harness.Controller.Pending(pairing.UserCode);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(0, harness.Throttle.ClientFailures);
        Assert.Equal(1, harness.Throttle.ClientResets);
    }

    [Fact]
    public void A_code_that_ran_out_of_time_is_charged_to_nobody()
    {
        using var harness = new Harness();
        var pairing = harness.OpenRequest();
        harness.Clock.Advance(TimeSpan.FromMinutes(6));

        var result = harness.Controller.Pending(pairing.UserCode);

        // The device whose code took too long to be confirmed is not the
        // device working through guesses, and must not be treated as one.
        Assert.Equal(409, StatusOf(result));
        Assert.Equal(0, harness.Throttle.ClientFailures);
    }

    [Fact]
    public void A_refusal_clears_the_client()
    {
        using var harness = new Harness();
        var pairing = harness.OpenRequest();

        var result = harness.Controller.Deny(new AnswerPairingRequest { UserCode = pairing.UserCode });

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(1, harness.Throttle.ClientResets);
        Assert.Equal(0, harness.Throttle.ClientFailures);
    }

    [Fact]
    public async Task An_approval_clears_the_client()
    {
        using var harness = new Harness();
        var pairing = harness.OpenRequest();

        var result = await harness.Controller.Approve(new AnswerPairingRequest { UserCode = pairing.UserCode });

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(1, harness.Throttle.ClientResets);
        Assert.Equal(0, harness.Throttle.ClientFailures);
    }

    // ──────────────────────────────────────────────
    //  Being shut out
    // ──────────────────────────────────────────────

    [Fact]
    public void A_locked_out_client_is_refused_before_the_code_is_read()
    {
        using var harness = new Harness();
        var pairing = harness.OpenRequest();
        harness.Throttle.ClientLockout = TimeSpan.FromMinutes(15);

        var result = harness.Controller.Pending(pairing.UserCode);

        // A real code, and still a refusal: the lockout is decided before
        // anything the caller could learn something from.
        Assert.Equal(429, StatusOf(result));
        Assert.Equal(0, harness.Throttle.ClientResets);
    }

    [Fact]
    public void A_locked_out_approver_is_refused_even_from_a_clean_address()
    {
        using var harness = new Harness();
        var pairing = harness.OpenRequest();
        harness.Throttle.UserLockout = TimeSpan.FromMinutes(15);

        var result = harness.Controller.Pending(pairing.UserCode);

        Assert.Equal(429, StatusOf(result));
    }

    [Fact]
    public void A_refusal_says_how_long_to_wait_in_the_header_and_the_body()
    {
        using var harness = new Harness();
        harness.Throttle.ClientLockout = TimeSpan.FromMinutes(15);

        var result = harness.Controller.Pending("BCDF-GHJK");

        // The approval page reads the message out of the body, and a client
        // that only reads headers gets the same answer.
        var body = Assert.IsType<VouchResponse>(ValueOf(result));
        Assert.NotNull(body.Message);
        Assert.NotNull(body.RetryAfter);
        Assert.Equal("900", harness.HttpContext.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void A_locked_out_client_cannot_open_a_pairing_request()
    {
        using var harness = new Harness();
        harness.Throttle.ClientLockout = TimeSpan.FromMinutes(15);

        var result = harness.Controller.RequestPairing(new RequestPairingRequest { DeviceName = "Living Room TV" });

        // Opening a request is a step towards a credential, so a client the
        // host has shut out of authenticating is shut out of this too.
        Assert.Equal(429, StatusOf(result));
    }

    [Fact]
    public void Opening_a_pairing_request_is_not_a_failed_attempt_at_anything()
    {
        using var harness = new Harness();

        var result = harness.Controller.RequestPairing(new RequestPairingRequest { DeviceName = "Living Room TV" });

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(0, harness.Throttle.ClientFailures);
        Assert.Equal(0, harness.Throttle.UserFailures);
    }

    [Fact]
    public void Polling_is_answered_even_while_the_address_is_locked_out()
    {
        using var harness = new Harness();
        var pairing = harness.OpenRequest();
        harness.Throttle.ClientLockout = TimeSpan.FromMinutes(15);

        var result = harness.Controller.Poll(new PollPairingRequest { DeviceCode = pairing.DeviceCode });

        // The device holds a 256-bit code it was issued rather than one it
        // guessed, and withholding a terminal answer from it would turn a
        // refusal or an expiry into silence. A poll is charged to nobody and
        // refused for nobody's lockout.
        Assert.Equal(200, StatusOf(result));
        Assert.Equal(0, harness.Throttle.ClientFailures);
        Assert.Equal(0, harness.Throttle.Checks);
    }

    [Fact]
    public void An_unknown_device_code_is_charged_to_nobody()
    {
        using var harness = new Harness();

        var result = harness.Controller.Poll(new PollPairingRequest { DeviceCode = new string('0', 64) });

        // A device polling a request the server forgot across a restart is
        // not guessing, and it would hit the ceiling within a minute if it
        // were counted as such.
        Assert.Equal(404, StatusOf(result));
        Assert.Equal(0, harness.Throttle.ClientFailures);
    }

    // ──────────────────────────────────────────────
    //  Shared
    // ──────────────────────────────────────────────

    private static int StatusOf(IConvertToActionResult result)
        => result.Convert() switch
        {
            ObjectResult objectResult => objectResult.StatusCode ?? 200,
            StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
            _ => 0,
        };

    private static object? ValueOf(IConvertToActionResult result)
        => result.Convert() is ObjectResult objectResult ? objectResult.Value : null;

    /// <summary>
    /// A controller wired to a real store, a fake throttle and a clock a
    /// test can move.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public TestTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

        public FakeAuthenticationThrottleService Throttle { get; } = new();

        public DefaultHttpContext HttpContext { get; }

        public PairingStore Store { get; }

        public VouchController Controller { get; }

        public Harness()
        {
            Store = new PairingStore(NullLogger<PairingStore>.Instance, _ => Task.CompletedTask, Clock);

            var user = new Mock<IUser>();
            user.SetupGet(u => u.Username).Returns(Approver);

            HttpContext = new DefaultHttpContext();
            HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ClientIp);

            var userService = new Mock<IUserService>();
            userService.Setup(s => s.GetUserFromHttpContext(It.IsAny<HttpContext>())).Returns(user.Object);
            userService.Setup(s => s.GetApiTokenFromHttpContext(It.IsAny<HttpContext>())).Returns((ApiToken?)null);
            userService.Setup(s => s.GetApiTokensForUser(It.IsAny<IUser>())).Returns([]);
            userService
                .Setup(s => s.GenerateApiTokenForUser(It.IsAny<IUser>(), It.IsAny<string>(), It.IsAny<DateTime>()))
                .ReturnsAsync((IUser owner, string device, DateTime expiresAt) => new ApiToken(owner, device, "issued-key", expiresAt));

            // ConfigurationProvider.Load goes through the non-generic overload
            // with whatever GetConfigurationInfo hands back, which for a loose
            // mock is nothing at all. Matching on that is enough here: the
            // provider is one of a kind in this harness.
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(s => s.Load(It.IsAny<ConfigurationInfo>(), It.IsAny<bool>()))
                .Returns(new VouchPluginConfiguration());

            Controller = new VouchController(
                userService.Object,
                Store,
                Throttle,
                new ConfigurationProvider<VouchPluginConfiguration>(configurationService.Object),
                NullLogger<VouchController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = HttpContext },
            };
        }

        /// <summary>Opens a pairing request the way a device would.</summary>
        public NewPairing OpenRequest()
            => Store.Create("Living Room TV", "TV", "TV/1.0", "10.0.0.5");

        public void Dispose()
        {
            Store.Dispose();
        }
    }
}
