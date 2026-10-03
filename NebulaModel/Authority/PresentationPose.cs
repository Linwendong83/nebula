#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// One authoritative placement sample: position, rotation and velocity at a host tick
/// (TASKS.md A14, pure model).
/// </summary>
/// <remarks>
/// The type carries pose only — no HP, shield, energy, inventory, kill or repair field —
/// so a presentation tick over these values cannot move a protected number by
/// construction. Ground floats upgrade to doubles losslessly, matching the sector-wide
/// double precision the space adapters already use for 1e9-scale coordinates.
/// </remarks>
public readonly struct AuthorityPose
{
    public AuthorityPose(double posX, double posY, double posZ,
        float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ, long hostTick)
    {
        PosX = posX;
        PosY = posY;
        PosZ = posZ;
        RotX = rotX;
        RotY = rotY;
        RotZ = rotZ;
        RotW = rotW;
        VelX = velX;
        VelY = velY;
        VelZ = velZ;
        HostTick = hostTick;
    }

    public double PosX { get; }

    public double PosY { get; }

    public double PosZ { get; }

    public float RotX { get; }

    public float RotY { get; }

    public float RotZ { get; }

    public float RotW { get; }

    public float VelX { get; }

    public float VelY { get; }

    public float VelZ { get; }

    /// <summary>Host tick this sample was taken on. Never a wall clock.</summary>
    public long HostTick { get; }
}

/// <summary>
/// Per-object display buffer separating the authority pose from the render pose
/// (DESIGN 10, TASKS.md A14, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The host record (<see cref="Push"/>) is the logic position the replica owns; the
/// renderer reads only what <see cref="TrySample"/> returns. Native pool mirrors stay the
/// UI/selection reference, while the display pose interpolates between the last two
/// authority samples. HP, death and repair are never extrapolated: this buffer has no such
/// fields, so "绝不外推 HP/死亡/维修" holds by construction.
/// </para>
/// <para>
/// Timing follows DESIGN 10 initial values: the display trails by
/// <see cref="BaseDelayTicks"/> (100 ms at 60 Hz) and extrapolates at most
/// <see cref="MaxExtrapolateTicks"/> (200 ms) past the newest sample before freezing.
/// These are presentation tuning, not rule inputs; A22 measures them.
/// </para>
/// </remarks>
public sealed class RenderPoseBuffer
{
    /// <summary>Display trails the newest authority sample by this long (100 ms at 60 Hz).</summary>
    public const long BaseDelayTicks = 6;

    /// <summary>Longest an object keeps moving past its newest sample before freezing.</summary>
    public const long MaxExtrapolateTicks = 12;

    private AuthorityPose previous;
    private AuthorityPose current;
    private bool hasPrevious;
    private bool hasCurrent;

    public bool HasSample => hasCurrent;

    public long NewestTick => hasCurrent ? current.HostTick : -1;

    /// <summary>Records one authoritative placement. Never mutates a previously pushed record.</summary>
    public void Push(in AuthorityPose authority)
    {
        if (authority.HostTick < 0) throw new ArgumentException("A pose needs a non-negative host tick.", nameof(authority));
        if (hasCurrent && authority.HostTick < current.HostTick)
            throw new ArgumentException("Poses must arrive in non-decreasing host-tick order.", nameof(authority));
        if (hasCurrent)
        {
            previous = current;
            hasPrevious = true;
        }
        current = authority;
        hasCurrent = true;
    }

    /// <summary>
    /// Samples the display pose for <paramref name="renderTick"/> (already delayed by the caller).
    /// </summary>
    /// <returns>False before the first push; otherwise the interpolated or frozen pose.</returns>
    public bool TrySample(long renderTick, out AuthorityPose display)
    {
        display = default;
        if (!hasCurrent || renderTick < 0) return false;
        if (!hasPrevious)
        {
            display = current;
            return true;
        }
        if (renderTick <= previous.HostTick)
        {
            display = previous;
            return true;
        }
        if (renderTick >= current.HostTick)
        {
            // At most 200 ms past the newest sample, then frozen: the overrun clamps, so the
            // display holds the extrapolation endpoint instead of inventing further motion.
            var over = Math.Min(renderTick - current.HostTick, MaxExtrapolateTicks);
            display = Extrapolate(current, previous, over);
            return true;
        }
        var span = current.HostTick - previous.HostTick;
        if (span <= 0)
        {
            display = current;
            return true;
        }
        var alpha = (double)(renderTick - previous.HostTick) / span;
        display = Interpolate(previous, current, alpha);
        return true;
    }

    public void Clear()
    {
        hasPrevious = false;
        hasCurrent = false;
        previous = default;
        current = default;
    }

    private static AuthorityPose Interpolate(in AuthorityPose from, in AuthorityPose to, double alpha)
    {
        if (alpha <= 0) return from;
        if (alpha >= 1) return to;
        var posX = from.PosX + (to.PosX - from.PosX) * alpha;
        var posY = from.PosY + (to.PosY - from.PosY) * alpha;
        var posZ = from.PosZ + (to.PosZ - from.PosZ) * alpha;
        var (rotX, rotY, rotZ, rotW) = Nlerp(from, to, (float)alpha);
        var velX = (float)(from.VelX + (to.VelX - from.VelX) * alpha);
        var velY = (float)(from.VelY + (to.VelY - from.VelY) * alpha);
        var velZ = (float)(from.VelZ + (to.VelZ - from.VelZ) * alpha);
        return new AuthorityPose(posX, posY, posZ, rotX, rotY, rotZ, rotW, velX, velY, velZ, to.HostTick);
    }

    private static AuthorityPose Extrapolate(in AuthorityPose newest, in AuthorityPose older, long over)
    {
        if (over <= 0) return newest;
        var span = newest.HostTick - older.HostTick;
        double stepX, stepY, stepZ;
        if (span > 0)
        {
            stepX = (newest.PosX - older.PosX) / span;
            stepY = (newest.PosY - older.PosY) / span;
            stepZ = (newest.PosZ - older.PosZ) / span;
        }
        else
        {
            stepX = newest.VelX / 60.0;
            stepY = newest.VelY / 60.0;
            stepZ = newest.VelZ / 60.0;
        }
        return new AuthorityPose(
            newest.PosX + stepX * over, newest.PosY + stepY * over, newest.PosZ + stepZ * over,
            newest.RotX, newest.RotY, newest.RotZ, newest.RotW,
            newest.VelX, newest.VelY, newest.VelZ, newest.HostTick + over);
    }

    private static (float X, float Y, float Z, float W) Nlerp(in AuthorityPose from, in AuthorityPose to, float alpha)
    {
        var dot = from.RotX * to.RotX + from.RotY * to.RotY + from.RotZ * to.RotZ + from.RotW * to.RotW;
        var sign = dot < 0 ? -1f : 1f;
        var x = from.RotX + (sign * to.RotX - from.RotX) * alpha;
        var y = from.RotY + (sign * to.RotY - from.RotY) * alpha;
        var z = from.RotZ + (sign * to.RotZ - from.RotZ) * alpha;
        var w = from.RotW + (sign * to.RotW - from.RotW) * alpha;
        var length = Math.Sqrt((double)x * x + y * y + z * z + w * w);
        if (length <= 1e-9)
        {
            return (to.RotX, to.RotY, to.RotZ, to.RotW);
        }
        var inv = 1.0 / length;
        return ((float)(x * inv), (float)(y * inv), (float)(z * inv), (float)(w * inv));
    }
}
