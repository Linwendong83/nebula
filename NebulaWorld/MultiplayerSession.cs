#region

using System;
using NebulaAPI.GameState;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaWorld.Combat;
using NebulaWorld.Factory;
using NebulaWorld.GameDataHistory;
using NebulaWorld.GameStates;
using NebulaWorld.Logistics;
using NebulaWorld.Planet;
using NebulaWorld.Player;
using NebulaWorld.Statistics;
using NebulaWorld.Trash;
using NebulaWorld.Universe;
using NebulaWorld.Warning;

#endregion

namespace NebulaWorld;

public class MultiplayerSession : IDisposable, IMultiplayerSession
{
    private bool canPause = true;

    // Some Patch Flags
    public DateTime StartTime;

    public MultiplayerSession(INetworkProvider networkProvider)
    {
        Network = networkProvider;
        if (networkProvider is IServer server)
            Server = server;

        if (networkProvider is IClient client)
            Client = client;

        LocalPlayer = new LocalPlayer();
        World = new SimulatedWorld();
        Combat = new CombatManager();
        Generations = new CombatGenerationManager();
        BattleVisuals = new BattleVisualManager();
        Impacts = new BattleImpactCapture();
        Life = new PlayerLifeManager();
        Enemies = new EnemyManager();
        Factories = new FactoryManager();
        Storage = new StorageManager();
        PowerTowers = new PowerTowerManager();
        Belts = new BeltManager();
        BuildTools = new BuildToolManager();
        BuildDispatch = new BuildDispatchManager();
        Drones = new DroneManager();
        Gizmos = new GizmoManager();
        History = new GameDataHistoryManager();
        State = new GameStatesManager();
        Goals = new GoalManager();
        Metadata = new MetadataManager();
        PropertyTransactions = new MetadataTransactionManager();
        Couriers = new CourierManager();
        Ships = new ILSShipManager();
        StationsUI = new StationUIManager();
        Planets = new PlanetManager();
        Statistics = new StatisticsManager();
        Kills = new KillStatisticsManager();
        Vegetation = new VegetationManager();
        Trashes = new TrashManager();
        Drops = new PersistentDropManager();
        DysonSpheres = new DysonSphereManager();
        Launch = new LaunchManager();
        Warning = new WarningManager();

        StartTime = DateTime.Now;
    }

    public SimulatedWorld World { get; set; }
    public CombatManager Combat { get; set; }
    public CombatGenerationManager Generations { get; set; }
    public BattleVisualManager BattleVisuals { get; set; }
    public BattleImpactCapture Impacts { get; set; }
    public PlayerLifeManager Life { get; set; }
    public EnemyManager Enemies { get; set; }
    public StorageManager Storage { get; set; }
    public PowerTowerManager PowerTowers { get; set; }
    public BeltManager Belts { get; set; }
    public BuildToolManager BuildTools { get; set; }
    public BuildDispatchManager BuildDispatch { get; set; }
    public DroneManager Drones { get; set; }
    public GizmoManager Gizmos { get; set; }
    public GameDataHistoryManager History { get; set; }
    public GameStatesManager State { get; set; }
    public GoalManager Goals { get; set; }
    public MetadataManager Metadata { get; set; }
    public MetadataTransactionManager PropertyTransactions { get; set; }
    public CourierManager Couriers { get; set; }
    public ILSShipManager Ships { get; set; }
    public StationUIManager StationsUI { get; set; }
    public PlanetManager Planets { get; set; }
    public StatisticsManager Statistics { get; set; }
    public KillStatisticsManager Kills { get; set; }
    public VegetationManager Vegetation { get; set; }
    public TrashManager Trashes { get; set; }
    public PersistentDropManager Drops { get; set; }
    public DysonSphereManager DysonSpheres { get; set; }
    public LaunchManager Launch { get; set; }
    public WarningManager Warning { get; set; }
    public bool IsInLobby { get; set; }

    public bool CanPause
    {
        get => canPause;
        set
        {
            canPause = value;
            SimulatedWorld.SetPauseIndicator(value);
        }
    }

    // A hosted session always has its human host in the world; a headless server is nobody's
    // mecha and must not reserve a player slot in the count.
    public ushort NumPlayers { get; set; } = Multiplayer.IsDedicated ? (ushort)0 : (ushort)1;

    public void Dispose()
    {
        Network?.Dispose();
        Network = null;

        LocalPlayer?.Dispose();
        LocalPlayer = null;

        World?.Dispose();
        World = null;

        Combat?.Dispose();
        Combat = null;
        Generations?.Dispose();
        Generations = null;
        BattleVisuals?.Dispose();
        BattleVisuals = null;
        Impacts?.Dispose();
        Impacts = null;
        Life?.Dispose();
        Life = null;
        Vegetation?.Dispose();
        Vegetation = null;

        Enemies?.Dispose();
        Enemies = null;

        Factories?.Dispose();
        Factories = null;

        Storage?.Dispose();
        Storage = null;

        PowerTowers?.Dispose();
        PowerTowers = null;

        Belts?.Dispose();
        Belts = null;

        BuildTools?.Dispose();
        BuildTools = null;

        BuildDispatch?.Dispose();
        BuildDispatch = null;

        Drones?.Dispose();
        Drones = null;

        Gizmos?.Dispose();
        Gizmos = null;

        History?.Dispose();
        History = null;

        State?.Dispose();
        State = null;
        Goals?.Dispose();
        Goals = null;
        Metadata?.Dispose();
        Metadata = null;
        PropertyTransactions?.Dispose();
        PropertyTransactions = null;

        Couriers?.Dispose();
        Couriers = null;

        Ships = null;

        StationsUI?.Dispose();
        StationsUI = null;

        Planets?.Dispose();
        Planets = null;

        Statistics?.Dispose();
        Statistics = null;
        Kills?.Dispose();
        Kills = null;

        Trashes?.Dispose();
        Trashes = null;
        Drops?.Dispose();
        Drops = null;

        DysonSpheres?.Dispose();
        DysonSpheres = null;

        Launch?.Dispose();
        Launch = null;

        Warning?.Dispose();
        Warning = null;

        GC.SuppressFinalize(this);
    }

    public INetworkProvider Network { get; set; }

    public IServer Server { get; set; }

    public IClient Client { get; set; }

    public ILocalPlayer LocalPlayer { get; set; }
    public IFactoryManager Factories { get; set; }
    public bool IsDedicated => Multiplayer.IsDedicated;
    public bool IsServer => Server is not null;
    public bool IsClient => Client is not null;

    public bool IsGameLoaded { get; set; }

    public void OnGameLoadCompleted()
    {
        if (IsGameLoaded)
        {
            return;
        }

        Log.Info("==== Game load completed ====");
        if (IsServer) SaveManager.EnsureServerDataLoaded();
        if (IsServer) SaveManager.BindWorldIdentity(GameMain.data);
        if (IsServer)
        {
            GoalManager.RestoreLevel(GameMain.data);
            Goals.LoadLegacyDefaults();
            Goals.PrepareHostProfile();
        }
        IsGameLoaded = true;
        if (IsServer) Metadata.Initialize();

        if (Multiplayer.Session.LocalPlayer.IsHost)
        {
            GameMain.history.universeObserveLevel = SimulatedWorld.GetUniverseObserveLevel();
        }

        if (Multiplayer.Session.LocalPlayer.IsInitialDataReceived)
        {
            Multiplayer.Session.World.SetupInitialPlayerState();
        }
        if (IsServer) BuildDispatch.InitializeLoadedFactories();
    }
}
