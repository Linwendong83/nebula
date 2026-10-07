using System;

namespace NebulaWorld.Combat;

public static class CombatTargetContext
{
    [ThreadStatic] public static Mecha TargetMecha;

    public static int TargetPlayerId()
    {
        if (!Multiplayer.IsActive) return 1;
        var mecha = TargetMecha ?? GameMain.spaceSector.skillSystem.mecha;
        foreach (var player in Multiplayer.Session.Combat.Players) if (player.mecha == mecha) return player.id;
        return Multiplayer.Session.LocalPlayer.Id;
    }

    public static bool IsPlayerTarget(int id)
    {
        if (!Multiplayer.IsActive) return id == 1;
        var session = Multiplayer.Session;
        if (!session.IsDedicated && id == session.LocalPlayer.Id) return true;
        return id > 0 && id <= ushort.MaxValue && session.CombatAuthority.MechaFor((ushort)id) != null;
    }

    public static Mecha ResolveDamageMecha(SkillSystem skills, int id)
    {
        if (!Multiplayer.IsActive) return skills.mecha;
        var session = Multiplayer.Session;
        if (!session.IsDedicated && id == session.LocalPlayer.Id) return GameMain.mainPlayer.mecha;
        return session.CombatAuthority.MechaFor((ushort)id) ?? throw new InvalidOperationException("Unknown authoritative combat target.");
    }
}
