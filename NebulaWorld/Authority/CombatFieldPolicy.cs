namespace NebulaWorld.Authority;

/// <summary>Classifies presentation fields; all other combat fields are host-owned facts.</summary>
public static class CombatFieldPolicy
{
    public static bool IsDisplayOnlyCombatField(string fieldName)
    {
        switch (fieldName)
        {
            // Blood-bar anchoring on the combat stat.
            case "localPos":
            case "size":
            // Impact bookkeeping used by the local renderer.
            case "lastImpact":
            // GPU renderer and collider handles are always local (DESIGN 4.1).
            case "modelId":
            case "colliderId":
                return true;
            default:
                return false;
        }
    }
}
