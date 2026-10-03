namespace NebulaModel.Authority;

using NebulaModel.Networking;

/// <summary>
/// What this installation offers and requires in a room (DESIGN 4.2).
/// </summary>
/// <remarks>
/// <para>
/// The installation always runs the host-authority mode in multiplayer rooms: there is no
/// development flag and no legacy room. Single-player still runs vanilla through the guard's
/// no-authority-world path, which is why the guard keeps its session-inactive allowance.
/// </para>
/// <para>
/// The values are process-wide on purpose. A client may not choose per-room whether the host
/// arbitrates damage (DESIGN 5.2), so there is no per-connection override to get wrong.
/// </para>
/// </remarks>
public static class AuthorityLocalOptions
{
    private static AuthorityCapability requiredCapabilities = AuthorityCapability.None;
    private static AuthorityCapability offeredCapabilities = AuthorityCapability.All;

    /// <summary>Mode this installation joins or hosts with. Always host-authority; no legacy rooms.</summary>
    public static AuthorityMode Mode => AuthorityMode.HostAuthority;

    /// <summary>Authority DTO schema. Always V1 while host-authority is the only mode.</summary>
    public static AuthoritySchema Schema => AuthoritySchema.V1;

    /// <summary>Capabilities the host advertises when running authority mode.</summary>
    public static AuthorityCapability OfferedCapabilities
    {
        get => offeredCapabilities;
        set => offeredCapabilities = value & AuthorityCapability.All;
    }

    /// <summary>Capabilities a client needs the host to provide. Must be a subset of the host's offer.</summary>
    public static AuthorityCapability RequiredCapabilities
    {
        get => requiredCapabilities;
        set => requiredCapabilities = value & AuthorityCapability.All;
    }

    /// <summary>The declaration string this installation sends in its handshake.</summary>
    public static string Declaration => AuthorityHandshake.Encode(Mode, Schema, requiredCapabilities);

    /// <summary>Restores the default authority capabilities. Used by tests and by leaving a room.</summary>
    public static void ResetToDefaults()
    {
        requiredCapabilities = AuthorityCapability.None;
        offeredCapabilities = AuthorityCapability.All;
    }
}
