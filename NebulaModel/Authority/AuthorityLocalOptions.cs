namespace NebulaModel.Authority;

using NebulaModel.Networking;

/// <summary>Process-wide replication schema and capability requirements.</summary>
public static class AuthorityLocalOptions
{
    private static AuthorityCapability requiredCapabilities = AuthorityCapability.None;
    public const AuthorityCapability ImplementedCapabilities = AuthorityCapability.Combat |
        AuthorityCapability.Snapshot | AuthorityCapability.Effects;
    private static AuthorityCapability offeredCapabilities = ImplementedCapabilities;

    /// <summary>Replication DTO schema supported by this installation.</summary>
    public static AuthoritySchema Schema => AuthoritySchema.V1;

    /// <summary>Capabilities the host advertises for world replication.</summary>
    public static AuthorityCapability OfferedCapabilities
    {
        get => offeredCapabilities;
        set => offeredCapabilities = value & ImplementedCapabilities;
    }

    /// <summary>Capabilities a client needs the host to provide. Must be a subset of the host's offer.</summary>
    public static AuthorityCapability RequiredCapabilities
    {
        get => requiredCapabilities;
        set => requiredCapabilities = value & AuthorityCapability.All;
    }

    /// <summary>The declaration string this installation sends in its handshake.</summary>
    public static string Declaration => AuthorityHandshake.Encode(Schema, requiredCapabilities);

    /// <summary>Restores the default authority capabilities. Used by tests and by leaving a room.</summary>
    public static void ResetToDefaults()
    {
        requiredCapabilities = AuthorityCapability.None;
        offeredCapabilities = ImplementedCapabilities;
    }
}
