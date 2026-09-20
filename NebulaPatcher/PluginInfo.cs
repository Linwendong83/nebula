namespace NebulaPatcher;

/// <summary>
///     The main metadata of the plugin.
///     This information is used for BepInEx plugin metadata.
/// </summary>
/// <remarks>
///     See also description of BepInEx metadata:
///     https://bepinex.github.io/bepinex_docs/master/articles/dev_guide/plugin_tutorial/2_plugin_start.html#basic-information-about-the-plug-in
/// </remarks>
internal static class PluginInfo
{
    /// <summary>
    ///     Human-readable name of the plugin. In general, it should be short and concise.
    ///     This is the name that is shown to the users who run BepInEx and to modders that inspect BepInEx logs.
    /// </summary>
    public const string PLUGIN_NAME = "NebulaMultiplayerMod";

    public const string PLUGIN_SHORT_NAME = "Nebula";

    /// <summary>
    ///     Unique ID of the plugin.
    ///     This must be a unique string that contains only characters a-z, 0-9 underscores (_) and dots (.)
    ///     Prefer using the reverse domain name notation: https://eqdn.tech/reverse-domain-notation/
    ///     When creating Harmony patches, prefer using this ID for Harmony instances as well.
    /// </summary>
    public const string PLUGIN_ID = "dsp.nebula-multiplayer";

    /// <summary>
    ///     Fork-specific dev suffix and revision number.
    ///     Empty string means pure upstream release build.
    /// </summary>
    public const string FORK_SUFFIX = "-dev.4";
    public const int FORK_REVISION = 4;

    /// <summary>
    ///     Base version from upstream (e.g. "0.9.22").
    /// </summary>
    public static readonly string BASE_VERSION = ThisAssembly.AssemblyVersion.EndsWith(".0")
        ? ThisAssembly.AssemblyVersion.Substring(0, ThisAssembly.AssemblyVersion.Length - 2)
        : ThisAssembly.AssemblyVersion;

    /// <summary>
    ///     Version of the plugin for BepInEx. Must be in form
    ///     <major>.<minor>.<build>.<revision>.
    ///     Major and minor versions are mandatory, but build and revision can be left unspecified.
    ///     BepInEx uses System.Version to parse this string, so it CANNOT contain non-numeric characters like "-dev.1".
    /// </summary>
    public const string PLUGIN_VERSION = ThisAssembly.AssemblyFileVersion;

    /// <summary>
    ///     Human-readable display version shown in UI, logs, error dialogs, and network handshake (e.g. "0.9.22-dev.1").
    /// </summary>
    public static readonly string PLUGIN_DISPLAY_VERSION = $"{BASE_VERSION}{FORK_SUFFIX}";
}
