using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NebulaModel.DataStructures;
using NebulaModel.Packets.Factory.PowerTower;
using NebulaWorld;
using NebulaWorld.Factory;
using UnityEngine;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

/// <summary>Managed charging behavior using real game method bodies, without starting Unity or a network session.</summary>
[TestClass]
public class PowerTowerBehaviorTest
{
    private GameData previousGame = null!;
    private MultiplayerSession previousSession = null!;
    private bool initialized;
    private PowerSystem power = null!;
    private Player player = null!;
    private Mecha mecha = null!;
    private PowerTowerManager towers = null!;
    private Harmony? transformShim;
    private static Vector3 testPosition;

    [TestInitialize]
    public void Initialize()
    {
        if ((typeof(PowerSystem).GetMethod("GameTick")!.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0) < 100)
        {
            TestAssert.AreNotEqual("1", Environment.GetEnvironmentVariable("NEBULA_REQUIRE_REAL_GAME"));
            TestAssert.Inconclusive("Run scripts/verify_power_towers.ps1 with a real game assembly.");
        }
        previousGame = GameMain.data;
        previousSession = Multiplayer.Session;
        initialized = true;

        var game = Uninitialized<GameData>();
        var planet = Uninitialized<PlanetData>();
        planet.id = 101;
        planet.radius = 200f;
        planet.scale = 1f;
        player = Uninitialized<Player>();
        mecha = Uninitialized<Mecha>();
        mecha.coreEnergy = 100;
        mecha.coreEnergyCap = 1000;
        mecha.energyChanges = new double[20];
        mecha.chargerDevice = new int[256];
        player.isAlive = true;
        AccessTools.Field(typeof(Player), "_planetId").SetValue(player, 101);
        SetBackingField(player, "mecha", mecha);
        SetBackingField(game, "mainPlayer", player);
        SetBackingField(game, "localPlanet", planet);
        var factory = Uninitialized<PlanetFactory>();
        SetBackingField(factory, "gameData", game);
        SetBackingField(factory, "planet", planet);
        power = Uninitialized<PowerSystem>();
        power.factory = factory;
        power.nodeCursor = 2;
        power.nodePool = new PowerNodeComponent[2];
        power.nodePool[1] = new PowerNodeComponent
        {
            id = 1,
            entityId = 42,
            networkId = 1,
            isCharger = true,
            coverRadius = 8.7f,
            powerPoint = new Vector3(0, 200.2f / 0.988f, 0),
            idleEnergyPerTick = 10,
            workEnergyPerTick = 110,
            requiredEnergy = 110
        };
        power.networkServes = [0, 0.5f];
        var session = Uninitialized<MultiplayerSession>();
        var playerData = Uninitialized<PlayerData>();
        playerData.PlayerId = 1;
        session.LocalPlayer = new LocalPlayer { Data = playerData };
        towers = new PowerTowerManager();
        session.PowerTowers = towers;
        Multiplayer.Session = session;
        GameMain.data = game;
        // Substitute only the Unity Transform read; all charging rules and vector math are real.
        transformShim = new Harmony("nebula.tests.player-position");
        transformShim.Patch(AccessTools.PropertyGetter(typeof(Player), "position"),
            prefix: new HarmonyMethod(typeof(PowerTowerBehaviorTest), nameof(GetTestPosition)));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!initialized) return;
        towers?.Dispose();
        transformShim?.UnpatchSelf();
        GameMain.data = previousGame;
        Multiplayer.Session = previousSession;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChargingUsesCorrectPositionRangeAltitudeAndPlayerState(bool multithreaded)
    {
        var near = new Vector3(0, 200.2f, 0);
        var far = new Vector3(200.2f, 0, 0);
        testPosition = multithreaded ? far : near;
        power.multithreadPlayerPos = multithreaded ? near : far;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsTrue(towers.IsLocalCharging(101, 1));
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(2, 101, [1]));
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(3, 102, [1]));
        TestAssert.AreEqual(2, towers.GetChargerCount(101, 1));

        mecha.coreEnergy = mecha.coreEnergyCap;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        TestAssert.AreEqual(1, towers.GetChargerCount(101, 1));
        mecha.coreEnergy = 100;
        player.isAlive = false;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        player.isAlive = true;

        foreach (var altitude in new[] { 100f, 251f })
        {
            testPosition = power.multithreadPlayerPos = new Vector3(0, altitude, 0);
            towers.UpdateLocalState(power, multithreaded);
            TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        }
        testPosition = power.multithreadPlayerPos = near;
        power.nodePool[1].powerPoint.x = 10.7f / 0.988f;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsTrue(towers.IsLocalCharging(101, 1));
        power.nodePool[1].powerPoint.x = 10.72f / 0.988f;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        power.nodePool[1].coverRadius = 15f;
        power.nodePool[1].powerPoint.x = 16f / 0.988f;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        power.nodePool[1].powerPoint.x = 0;
        towers.UpdateLocalState(power, multithreaded);
        TestAssert.IsTrue(towers.IsLocalCharging(101, 1));
        towers.ClearLocalState();
        TestAssert.IsFalse(towers.IsLocalCharging(101, 1));
        TestAssert.AreEqual(1, towers.GetChargerCount(102, 1));
    }

    [TestMethod]
    public void RemoteDemandNeverChargesLocalMechaAndSinglePlayerKeepsVanillaEnergy()
    {
        var type = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType("NebulaPatcher.Patches.Transpilers.PowerSystem_Transpiler")!;
        var demand = type.GetMethod("SetChargerRequiredPower", BindingFlags.Static | BindingFlags.NonPublic)!;
        var charge = type.GetMethod("AddMechaEnergy", BindingFlags.Static | BindingFlags.NonPublic)!;
        towers.ApplyRemoteState(new PowerTowerChargerUpdate(2, 101, [1]));
        object[] args = [power, power.nodePool[1]];
        TestAssert.IsTrue((bool)demand.Invoke(null, args)!);
        TestAssert.AreEqual(110, ((PowerNodeComponent)args[1]).requiredEnergy);
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(100d, mecha.coreEnergy);
        TestAssert.AreEqual(0, mecha.chargerCount);

        testPosition = new Vector3(0, 200.2f, 0);
        towers.UpdateLocalState(power, false);
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(150d, mecha.coreEnergy); // (110 - 10) * 50% supplied power.
        TestAssert.AreEqual(50d, mecha.energyChanges[2]);
        TestAssert.AreEqual(42, mecha.chargerDevice[0]);
        AccessTools.Field(typeof(Player), "_planetId").SetValue(player, 102);
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(150d, mecha.coreEnergy);

        Multiplayer.Session = null!;
        args[1] = new PowerNodeComponent { requiredEnergy = 57 };
        TestAssert.IsFalse((bool)demand.Invoke(null, args)!);
        TestAssert.AreEqual(57, ((PowerNodeComponent)args[1]).requiredEnergy);
        mecha.coreEnergy = 990;
        charge.Invoke(null, [power, 1]);
        TestAssert.AreEqual(1000d, mecha.coreEnergy);
        TestAssert.AreEqual(100d, mecha.energyChanges[2]);
    }

    private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

    private static bool GetTestPosition(ref Vector3 __result)
    {
        __result = testPosition;
        return false;
    }

    private static void SetBackingField(object instance, string property, object value) =>
        AccessTools.Field(instance.GetType(), "<" + property + ">k__BackingField").SetValue(instance, value);
}
