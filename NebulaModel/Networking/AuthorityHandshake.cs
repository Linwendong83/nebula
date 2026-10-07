using NebulaModel.Authority;

namespace NebulaModel.Networking;

/// <summary>Schema and capability declaration carried in the handshake's mod-version list.</summary>
public static class AuthorityHandshake
{
    public const string Prefix = "1";

    /// <summary>Formats <c>prefix;schema;requiredCapabilities</c>.</summary>
    public static string Encode(AuthoritySchema schema, AuthorityCapability requiredCapabilities) =>
        Prefix + ";" + (byte)schema + ";" + (uint)requiredCapabilities;

    /// <summary>Refuses incomplete declarations, unsupported schemas and unknown capability bits.</summary>
    public static bool TryDecode(string value, out AuthoritySchema schema,
        out AuthorityCapability requiredCapabilities)
    {
        schema = AuthoritySchema.None;
        requiredCapabilities = AuthorityCapability.None;
        if (string.IsNullOrEmpty(value)) return false;

        var parts = value.Split(';');
        if (parts.Length != 3 || parts[0] != Prefix) return false;
        if (!byte.TryParse(parts[1], out var rawSchema) || rawSchema != (byte)AuthoritySchema.V1) return false;
        if (!uint.TryParse(parts[2], out var rawCapabilities)) return false;
        if (!AuthorityNegotiation.AreCapabilitiesKnown((AuthorityCapability)rawCapabilities)) return false;

        schema = (AuthoritySchema)rawSchema;
        requiredCapabilities = (AuthorityCapability)rawCapabilities;
        return true;
    }
}
