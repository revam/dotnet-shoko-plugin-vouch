using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Abstractions.User.Services;
using Shoko.Plugin.Vouch.Services;

namespace Shoko.Plugin.Vouch;

/// <inheritdoc/>
public class Plugin : IPlugin, IPluginServiceRegistration
{
    /// <inheritdoc/>
    public Guid ID { get; private init; } = Constants.PluginGuid;

    /// <inheritdoc/>
    public string Name { get; private set; } = Constants.PluginName;

    /// <inheritdoc/>
    public string Description { get; private set; } = """
        Sign in a second device by showing it a code.
        A device that is awkward to type on displays a short code; a phone
        that is already signed in scans it and confirms, and the first device
        receives its own API key. The code carries a request rather than a
        credential, so a photograph of the screen is worth nothing on its own.
    """;

    /// <inheritdoc/>
    public IReadOnlyList<PluginPage> GetPages() =>
    [
        new()
        {
            Name = "Pair a device",
            Url = Constants.ApprovalPath,
        },
    ];

    /// <inheritdoc/>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        // The store is handed a revoke callback rather than the user
        // service, so the state machine has no host in it and can be tested
        // without one. The only thing it revokes is a key it caused to be
        // minted and that nobody collected.
        serviceCollection.AddSingleton(provider => new PairingStore(
            provider.GetRequiredService<ILogger<PairingStore>>(),
            apiKey => provider.GetRequiredService<IUserService>().InvalidateApiToken(apiKey)));
    }
}
