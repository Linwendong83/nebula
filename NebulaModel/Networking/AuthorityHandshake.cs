using NebulaModel.Authority;

namespace NebulaModel.Networking;

/// <summary>
/// The authority declaration a peer sends in its handshake.
/// </summary>
/// <remarks>
/// <para>
/// It is encoded as a short string and carried in the existing mod-version list under
/// <see cref="SessionProtocol.AuthorityHandshakeKey"/>. That placement is deliberate: the list is
/// already checked for unknown entries and version mismatches, so an old host refuses the peer
/// instead of accepting it while ignoring a declaration it cannot read.
/// </para>
/// <para>
/// The declaration is what makes "旧/新模式拒绝混房" a handshake outcome rather than a runtime
/// surprise: <see cref="AuthorityNegotiation.IsCompatible"/> decides before the client is admitted.
/// </para>
/// </remarks>
public static class AuthorityHandshake
{
    /// <summary>Version prefix of the declaration string. Bump only with a new declaration shape.</summary>
    public const string Prefix = "1";

    /// <summary>Formats a declaration: <c>1;mode;schema;requiredCapabilities</c>.</summary>
    public static string Encode(AuthorityMode mode, AuthoritySchema schema, AuthorityCapability requiredCapabilities) =>
        Prefix + ";" + (byte)mode + ";" + (byte)schema + ";" + (uint)requiredCapabilities;

    /// <summary>
    /// Parses a declaration, refusing anything it cannot fully understand.
    /// </summary>
    /// <remarks>
    /// An unknown prefix, an unknown mode or schema value, or a capability bit outside the defined
    /// set all fail here. Accepting an unknown value would be exactly the silent fallback DESIGN 1.8
    /// forbids.
    /// </remarks>
    public static bool TryDecode(string value, out AuthorityMode mode, out AuthoritySchema schema,
        out AuthorityCapability requiredCapabilities)
    {
        mode = AuthorityMode.None;
        schema = AuthoritySchema.None;
        requiredCapabilities = AuthorityCapability.None;
        if (string.IsNullOrEmpty(value)) return false;

        var parts = value.Split(';');
        if (parts.Length != 4) return false;
        if (parts[0] != Prefix) return false;
        if (!byte.TryParse(parts[1], out var rawMode) || !byte.TryParse(parts[2], out var rawSchema)) return false;
        if (!uint.TryParse(parts[3], out var rawCapabilities)) return false;
        if (!System.Enum.IsDefined(typeof(AuthorityMode), rawMode)) return false;
        if (!System.Enum.IsDefined(typeof(AuthoritySchema), rawSchema)) return false;
        if (!AuthorityNegotiation.AreCapabilitiesKnown((AuthorityCapability)rawCapabilities)) return false;

        mode = (AuthorityMode)rawMode;
        schema = (AuthoritySchema)rawSchema;
        requiredCapabilities = (AuthorityCapability)rawCapabilities;
        return true;
    }
}
