using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NebulaModel.DataStructures;

public enum BattleEffectKind : byte
{
    GroundLaser, GroundPlasma, GroundShieldPlasma, SpaceLaser, SpacePlasmaF, SpacePlasmaA,
    TurretMissile, TurretPlasma, LancerSweep, BomberProjectile, Impact
}

public sealed class FleetVisualData
{
    public int Id;
    public long Generation;
    public int Model;
    public int Astro;
    public bool Space;
    public VectorLF3 Position;
    public Quaternion Rotation;
    public AnimData Animation;
}

public sealed class BattleEffectData
{
    public BattleEffectKind Kind;
    public int Id;
    public long Generation;
    public int Subtype;
    public int Life;
    public byte[] Payload;
}

public sealed class BattleVisualFrame
{
    public const int MaxUnits = 4096;
    public const int MaxEffects = 16384;
    public List<FleetVisualData> Units { get; } = new();
    public List<BattleEffectData> Effects { get; } = new();

    public byte[] Export()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(1); w.Write(Units.Count);
        foreach (var unit in Units)
        {
            w.Write(unit.Id); w.Write(unit.Generation); w.Write(unit.Model); w.Write(unit.Astro); w.Write(unit.Space);
            w.Write(unit.Position.x); w.Write(unit.Position.y); w.Write(unit.Position.z);
            w.Write(unit.Rotation.x); w.Write(unit.Rotation.y); w.Write(unit.Rotation.z); w.Write(unit.Rotation.w);
            w.Write(unit.Animation.time); w.Write(unit.Animation.prepare_length); w.Write(unit.Animation.working_length);
            w.Write(unit.Animation.state); w.Write(unit.Animation.power);
        }
        w.Write(Effects.Count);
        foreach (var effect in Effects)
        {
            w.Write((byte)effect.Kind); w.Write(effect.Id); w.Write(effect.Generation); w.Write(effect.Subtype);
            w.Write(effect.Life); w.Write(effect.Payload.Length); w.Write(effect.Payload);
        }
        return stream.ToArray();
    }

    public static BattleVisualFrame Import(byte[] bytes)
    {
        if (bytes == null || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid visual frame size");
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        if (r.ReadInt32() != 1) throw new InvalidDataException("Unknown battle visual format");
        var frame = new BattleVisualFrame();
        var count = r.ReadInt32();
        if (count < 0 || count > MaxUnits) throw new InvalidDataException("Invalid fleet count");
        for (var i = 0; i < count; i++)
        {
            var unit = new FleetVisualData
            {
                Id = r.ReadInt32(),
                Generation = r.ReadInt64(),
                Model = r.ReadInt32(),
                Astro = r.ReadInt32(),
                Space = r.ReadBoolean(),
                Position = new VectorLF3(ReadFinite(r), ReadFinite(r), ReadFinite(r)),
                Rotation = new Quaternion(ReadFloat(r), ReadFloat(r), ReadFloat(r), ReadFloat(r)),
                Animation = new AnimData
                {
                    time = ReadFloat(r),
                    prepare_length = ReadFloat(r),
                    working_length = ReadFloat(r),
                    state = r.ReadUInt32(),
                    power = ReadFloat(r)
                }
            };
            if (unit.Id <= 0 || unit.Generation <= 0 || unit.Model <= 0 || unit.Model >= 2048)
                throw new InvalidDataException("Invalid fleet visual identity");
            frame.Units.Add(unit);
        }
        count = r.ReadInt32();
        if (count < 0 || count > MaxEffects) throw new InvalidDataException("Invalid effect count");
        for (var i = 0; i < count; i++)
        {
            var effect = new BattleEffectData
            {
                Kind = (BattleEffectKind)r.ReadByte(),
                Id = r.ReadInt32(),
                Generation = r.ReadInt64(),
                Subtype = r.ReadInt32(),
                Life = r.ReadInt32()
            };
            var length = r.ReadInt32();
            if (effect.Kind > BattleEffectKind.Impact || effect.Id <= 0 || effect.Life < 0 || effect.Life > 360000 ||
                length < 0 || length > 4096 || length > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid combat effect");
            effect.Payload = r.ReadBytes(length);
            frame.Effects.Add(effect);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing battle visual bytes");
        return frame;
    }

    private static double ReadFinite(BinaryReader r)
    {
        var value = r.ReadDouble();
        if (double.IsInfinity(value) || double.IsNaN(value) || Math.Abs(value) > 1e15)
            throw new InvalidDataException("Invalid visual position");
        return value;
    }
    private static float ReadFloat(BinaryReader r)
    {
        var value = r.ReadSingle();
        if (float.IsInfinity(value) || float.IsNaN(value)) throw new InvalidDataException("Invalid visual value");
        return value;
    }
}
