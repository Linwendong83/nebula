using NebulaModel.DataStructures;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.NebulaModel;

[TestClass]
public class BattleVisualFrameTest
{
    [TestMethod]
    public void FleetFramesKeepUniversalCoordinatesAndIndependentAnimationIdentity()
    {
        var frame = new BattleVisualFrame();
        frame.Units.Add(new FleetVisualData
        {
            Id = 7,
            Generation = 99,
            Model = 443,
            Astro = 100,
            Space = true,
            Position = new VectorLF3 { x = 123456789012.5, y = -8000, z = 42 },
            Rotation = new Quaternion { w = 1 },
            Animation = new AnimData { time = 0.5f, state = 2, power = 1 }
        });
        var restored = BattleVisualFrame.Import(frame.Export());
        TestAssert.AreEqual(123456789012.5, restored.Units[0].Position.x);
        TestAssert.AreEqual(99L, restored.Units[0].Generation);
        TestAssert.AreEqual(2U, restored.Units[0].Animation.state);
    }

    [TestMethod]
    public void EmptyFrameCanClearRemoteFleetAndPayloadIsLengthDelimited()
    {
        TestAssert.IsEmpty(BattleVisualFrame.Import(new BattleVisualFrame().Export()).Units);
        var frame = new BattleVisualFrame();
        frame.Effects.Add(new BattleEffectData
        { Kind = BattleEffectKind.TurretMissile, Id = 9, Generation = 20, Life = 60, Payload = [1, 2, 3] });
        var restored = BattleVisualFrame.Import(frame.Export());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, restored.Effects[0].Payload);
        var bytes = frame.Export();
        TestAssert.Throws<System.IO.InvalidDataException>(() => BattleVisualFrame.Import(bytes.Take(bytes.Length - 2).ToArray()));
    }

    [TestMethod]
    public void InvalidCoordinatesNeverReachGpuBuffers()
    {
        var frame = new BattleVisualFrame();
        frame.Units.Add(new FleetVisualData
        { Id = 1, Generation = 1, Model = 443, Position = new VectorLF3 { x = double.NaN } });
        TestAssert.Throws<System.IO.InvalidDataException>(() => BattleVisualFrame.Import(frame.Export()));
    }
}
