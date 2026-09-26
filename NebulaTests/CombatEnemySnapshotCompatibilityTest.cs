using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NebulaWorld;
using NebulaWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
public class CombatEnemySnapshotCompatibilityTest
{
    [TestMethod]
    [DoNotParallelize]
    public void SpaceEnemySnapshotOverlaysTheCurrentInstance()
    {
        var oldData = GameMain.data;
        var oldSession = Multiplayer.Session;
        try
        {
            var gameData = (GameData)FormatterServices.GetUninitializedObject(typeof(GameData));
            var sector = (SpaceSector)FormatterServices.GetUninitializedObject(typeof(SpaceSector));
            var galaxy = (GalaxyData)FormatterServices.GetUninitializedObject(typeof(GalaxyData));
            galaxy.starCount = 1;
            sector.galaxy = galaxy;
            sector.maxHiveCount = 10;
            sector.dfHivesByAstro = new EnemyDFHiveSystem[10];
            sector.dfHivesByAstro[1] = (EnemyDFHiveSystem)FormatterServices.GetUninitializedObject(
                typeof(EnemyDFHiveSystem));
            sector.enemyPool = new EnemyData[8];
            sector.enemyPool[7] = new EnemyData
            {
                id = 7,
                protoId = 8110,
                modelIndex = 450,
                originAstroId = 1000001,
                astroId = 1000001
            };
            sector.skillSystem = (SkillSystem)FormatterServices.GetUninitializedObject(typeof(SkillSystem));
            gameData.spaceSector = sector;
            GameMain.data = gameData;

            var session = (MultiplayerSession)FormatterServices.GetUninitializedObject(typeof(MultiplayerSession));
            session.Server = (global::NebulaModel.Networking.IServer)FormatterServices.GetUninitializedObject(
                typeof(global::NebulaNetwork.Server));
            Multiplayer.Session = session;

            sector.enemyPool[7].pos.x = 1234;
            var snapshot = new EnemyManager().ExportEnemySnapshot(0, 7);
            TestAssert.IsNotNull(snapshot);
            sector.enemyPool[7].pos.x = 0;
            var response = new global::NebulaModel.Packets.Combat.CombatEnemyStateResponsePacket
            {
                AstroId = 0,
                EnemyId = 7,
                Generation = 2,
                Alive = true,
                OriginAstroId = 1000001,
                ProtoId = 8110,
                ModelIndex = 450,
                Snapshot = snapshot
            };
            var apply = typeof(EnemyManager).GetMethod("ApplyEnemySnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            TestAssert.IsTrue((bool)apply.Invoke(new EnemyManager(), [response])!);
            TestAssert.AreEqual(7, sector.enemyPool[7].id);
            TestAssert.AreEqual(1234.0, sector.enemyPool[7].pos.x);
        }
        finally
        {
            Multiplayer.Session = oldSession;
            GameMain.data = oldData;
        }
    }

    [TestMethod]
    public void NativeComponentSnapshotPreservesLocalPoolId()
    {
        var export = typeof(EnemyManager).GetMethod("ExportComponent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var import = typeof(EnemyManager).GetMethod("ImportComponent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var source = (EnemyDFGroundSystem)FormatterServices.GetUninitializedObject(typeof(EnemyDFGroundSystem));
        source.builders = (DataPool<EnemyBuilderComponent>)FormatterServices.GetUninitializedObject(
            typeof(DataPool<EnemyBuilderComponent>));
        source.builders.buffer = new EnemyBuilderComponent[2];
        source.builders.buffer[1] = new EnemyBuilderComponent { id = 1, enemyId = 7, builderIndex = 12 };
        var bytes = (byte[])export.Invoke(null, [source, "builders", 1])!;
        TestAssert.IsGreaterThan(0, bytes.Length);

        var destination = (EnemyDFGroundSystem)FormatterServices.GetUninitializedObject(typeof(EnemyDFGroundSystem));
        destination.builders = (DataPool<EnemyBuilderComponent>)FormatterServices.GetUninitializedObject(
            typeof(DataPool<EnemyBuilderComponent>));
        destination.builders.buffer = new EnemyBuilderComponent[3];
        destination.builders.buffer[2] = new EnemyBuilderComponent { id = 2, enemyId = 7 };
        TestAssert.IsTrue((bool)import.Invoke(null, [destination, "builders", 2, 7, bytes])!);
        TestAssert.AreEqual(2, destination.builders.buffer[2].id);
        TestAssert.AreEqual(7, destination.builders.buffer[2].enemyId);
        TestAssert.AreEqual(12, destination.builders.buffer[2].builderIndex);
    }

    [TestMethod]
    [DoNotParallelize]
    public void ClientZeroHpGuardKeepsTheEnemyTargetable()
    {
        var zeroHp = AccessTools.Method(typeof(CombatStat), nameof(CombatStat.HandleZeroHp));
        if ((zeroHp.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0) <= 5)
            TestAssert.Inconclusive("Run with the game's Assembly-CSharp.dll, not reference stubs.");

        var oldData = GameMain.data;
        var oldSession = Multiplayer.Session;
        try
        {
            var gameData = (GameData)FormatterServices.GetUninitializedObject(typeof(GameData));
            var sector = (SpaceSector)FormatterServices.GetUninitializedObject(typeof(SpaceSector));
            sector.enemyPool = new EnemyData[8];
            sector.enemyPool[7].id = 7;
            gameData.spaceSector = sector;
            GameMain.data = gameData;

            var session = (MultiplayerSession)FormatterServices.GetUninitializedObject(typeof(MultiplayerSession));
            session.Client = (global::NebulaModel.Networking.IClient)FormatterServices.GetUninitializedObject(typeof(global::NebulaNetwork.Client));
            session.Enemies = new EnemyManager();
            session.Generations = new CombatGenerationManager();
            session.Combat = new CombatManager();
            Multiplayer.Session = session;

            var stat = new CombatStat
            {
                objectType = (int)EObjectType.Enemy,
                objectId = 7,
                originAstroId = 1000001,
                hp = 0
            };
            var patchType = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
                "NebulaPatcher.Patches.Dynamic.CombatStat_Patch", true)!;
            var prefix = patchType.GetMethod("HandleZeroHp_Prefix", BindingFlags.Static | BindingFlags.Public)!;
            object[] args = [stat];
            var runNative = (bool)prefix.Invoke(null, args)!;
            stat = (CombatStat)args[0];

            TestAssert.IsFalse(runNative);
            TestAssert.AreEqual(1, stat.hp);
            TestAssert.AreEqual(7, sector.enemyPool[7].id);
            TestAssert.IsFalse(sector.enemyPool[7].isInvincible);

            var factory = (PlanetFactory)FormatterServices.GetUninitializedObject(typeof(PlanetFactory));
            factory.enemyPool = new EnemyData[8];
            factory.enemyPool[7].id = 7;
            var groundPatch = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
                "NebulaPatcher.Patches.Dynamic.PlanetFactory_patch", true)!;
            var groundPrefix = groundPatch.GetMethod("KillEnemyFinally_Prefix", BindingFlags.Static | BindingFlags.Public)!;
            TestAssert.IsFalse((bool)groundPrefix.Invoke(null, [factory, 7])!);
            TestAssert.AreEqual(7, factory.enemyPool[7].id);
            TestAssert.IsFalse(factory.enemyPool[7].isInvincible);

            var spacePatch = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
                "NebulaPatcher.Patches.Dynamic.SpaceSector_Patch", true)!;
            var spacePrefix = spacePatch.GetMethod("KillEnemyFinal_Prefix", BindingFlags.Static | BindingFlags.Public)!;
            TestAssert.IsFalse((bool)spacePrefix.Invoke(null, [sector, 7])!);
            TestAssert.AreEqual(7, sector.enemyPool[7].id);
            TestAssert.IsFalse(sector.enemyPool[7].isInvincible);

            var skillSystem = (SkillSystem)FormatterServices.GetUninitializedObject(typeof(SkillSystem));
            var stats = (DataPool<CombatStat>)FormatterServices.GetUninitializedObject(typeof(DataPool<CombatStat>));
            stats.buffer = new CombatStat[2];
            stats.cursor = 2;
            stats.buffer[1] = new CombatStat
            {
                id = 1,
                objectType = (int)EObjectType.Enemy,
                objectId = 7,
                originAstroId = 1000001,
                hp = 1,
                hpMax = 1000
            };
            skillSystem.combatStats = stats;
            sector.skillSystem = skillSystem;
            sector.enemyPool[7].combatStatId = 1;
            sector.enemyPool[7].originAstroId = 1000001;
            sector.enemyPool[7].protoId = 8113;
            var response = new global::NebulaModel.Packets.Combat.CombatEnemyStateResponsePacket
            {
                EnemyId = 7,
                OriginAstroId = 1000001,
                ProtoId = 8113,
                HasCombatStat = true,
                Hp = 500,
                HpMax = 1000
            };
            var health = typeof(EnemyManager).GetMethod("ApplyAuthoritativeHealth",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            health.Invoke(null, [0, response]);
            TestAssert.AreEqual(500, stats.buffer[1].hp);

            using var generations = new MemoryStream();
            using (var writer = new BinaryWriter(generations, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(0);
                writer.Write(7);
                writer.Write(2L);
            }
            session.Generations.Import(generations.ToArray());
            var staleDeath = new global::NebulaModel.Packets.Combat.CombatEnemyStateResponsePacket
            {
                AstroId = 0,
                EnemyId = 7,
                ExpectedGeneration = 1,
                Generation = 1,
                Alive = false
            };
            var applyState = typeof(EnemyManager).GetMethod("ApplyAuthoritativeState",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            applyState.Invoke(session.Enemies, [staleDeath]);
            TestAssert.AreEqual(7, sector.enemyPool[7].id);
        }
        finally
        {
            Multiplayer.Session = oldSession;
            GameMain.data = oldData;
        }
    }

    [TestMethod]
    public void NativeEnemySnapshotContractsAreAvailable()
    {
        var zeroHp = AccessTools.Method(typeof(CombatStat), nameof(CombatStat.HandleZeroHp));
        TestAssert.IsNotNull(zeroHp);
        if ((zeroHp.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0) <= 5)
            TestAssert.Inconclusive("Run with the game's Assembly-CSharp.dll, not reference stubs.");

        foreach (var type in new[]
        {
            typeof(EnemyData), typeof(EnemyBuilderComponent), typeof(EnemyUnitComponent),
            typeof(DFGBaseComponent), typeof(DFGConnectorComponent), typeof(DFGReplicatorComponent),
            typeof(DFGTurretComponent), typeof(DFGShieldComponent), typeof(DFSCoreComponent),
            typeof(DFSNodeComponent), typeof(DFSConnectorComponent), typeof(DFSReplicatorComponent),
            typeof(DFSGammaComponent), typeof(DFSTurretComponent), typeof(DFTinderComponent),
            typeof(DFRelayComponent)
        })
        {
            TestAssert.IsNotNull(AccessTools.Method(type, "Export", [typeof(BinaryWriter)]), type.Name);
            TestAssert.IsNotNull(AccessTools.Method(type, "Import", [typeof(BinaryReader)]), type.Name);
        }

        foreach (var (system, names) in new[]
        {
            (typeof(EnemyDFGroundSystem), new[] { "builders", "bases", "connectors", "replicators", "turrets", "shields", "units" }),
            (typeof(EnemyDFHiveSystem), new[] { "builders", "cores", "nodes", "connectors", "replicators", "gammas", "turrets", "tinders", "relays", "units" })
        })
        {
            foreach (var name in names)
            {
                var pool = AccessTools.Field(system, name);
                TestAssert.IsNotNull(pool, system.Name + "." + name);
                TestAssert.IsNotNull(AccessTools.Field(pool.FieldType, "buffer"), pool.FieldType.Name);
            }
        }

        var enemy = new EnemyData { id = 17, protoId = 8128, originAstroId = 101 };
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) enemy.Export(writer);
        stream.Position = 0;
        var restored = new EnemyData();
        using (var reader = new BinaryReader(stream)) restored.Import(reader);
        TestAssert.AreEqual(enemy.id, restored.id);
        TestAssert.AreEqual(enemy.protoId, restored.protoId);
        TestAssert.AreEqual(enemy.originAstroId, restored.originAstroId);
    }
}
