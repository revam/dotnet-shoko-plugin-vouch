using System;

namespace Shoko.Plugin.Vouch;

/// <summary>
/// The handful of values that more than one file has to agree on.
/// </summary>
public static class Constants
{
    /// <summary>The plugin's unique identifier.</summary>
    public static readonly Guid PluginGuid = new("d88779b6-3551-4b07-a05e-595b6e167b13");

    /// <summary>The plugin's name, which is also its API route segment.</summary>
    public const string PluginName = "Vouch";

    /// <summary>
    /// Where a second device goes to answer a pairing request.
    ///
    /// It is a path rather than a URL because the host part depends on how
    /// the requesting device reached the server, and only a request knows
    /// that.
    /// </summary>
    public const string ApprovalPath = "/plugin/Vouch/Approve";
}
