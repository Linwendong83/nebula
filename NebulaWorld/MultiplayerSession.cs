#region

using System;
using NebulaAPI.GameState;
using NebulaAPI.Networking;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaWorld.Authority;
using NebulaWorld.Authority.Adapters;
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
        CombatAuthority = new CombatAuthorityManager();
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
        Authority = new AuthoritySessionState();
        AuthorityRuntime = new AuthoritySession(Authority) { RequireGamePools = true };
        // The A05 rule guard needs to see this session's mode and apply window; installing here keeps
        // every caller (game patches, packet processors, world managers) on one decision point.
        AuthorityGuardWiring.Install();

        StartTime = DateTime.Now;
    }

    public SimulatedWorld World { get; set; }
    public CombatManager Combat { get; set; }
    public CombatAuthorityManager CombatAuthority { get; set; }
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

    /// <summary>Authority identity the packet gate validates against (A03).</summary>
    public AuthoritySessionState Authority { get; set; }

    /// <summary>Authority runtime: command inbox, replica apply window and frame boundary (A04).</summary>
    public AuthoritySession AuthorityRuntime { get; set; }

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
        CombatAuthority?.Dispose();
        CombatAuthority = null;
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

        AuthorityRuntime?.Dispose();
        AuthorityRuntime = null;

        Authority?.Reset();
        Authority = null;
        AuthorityRuleGuard.ResetCounters();

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

        // A loaded host world gets its authority identity here, once, so every peer keys its
        // commands and objects to the same world. The client adopts the epoch from the host's
        // welcome instead; until then it has none and the gate refuses authority packets.
        if (IsServer)
        {
            AuthorityRuntime?.BeginAuthorityWorld(AuthorityEpoch.New(), isHost: true);

            if (AuthorityRuntime?.IsHostAuthority == true)
            {
                var epoch = AuthorityRuntime.Identity.Epoch;
                var entities = new FactoryCombatSnapshotAdapter(epoch);
                var groundEnemies = new GroundEnemySnapshotAdapter(epoch);
                var spaceEnemies = new SpaceEnemySnapshotAdapter(epoch);
                var crafts = new CraftSnapshotAdapter(epoch);
                var darkFog = new DarkFogSnapshotAdapter(epoch);
                var worldView = new CompositeHostWorldView(
                    entities,
                    groundEnemies,
                    spaceEnemies,
                    crafts,
                    darkFog);
                CombatAuthority.TargetWorld = worldView;
                AuthorityRuntime.HostExecutor = CombatAuthority;
                AuthorityRuntime.BeforeHostFrame = CombatAuthority.BeginFrame;
                AuthorityRuntime.AfterHostFrame = CombatAuthority.CompleteFrame;
                AuthorityRuntime.SubscriptionPlanetProvider = playerId =>
                {
                    var player = Server?.Players?.Get(playerId) ?? Server?.Players?.Get(playerId, EConnectionStatus.Syncing);
                    return player == null ? (int?)null : System.Math.Max(0, player.Data.LocalPlanetId);
                };
                AuthorityRuntime.CommandResultSink = (command, disposition, outcome) =>
                {
                    var player = Server?.Players?.Get(command.ConnectionPlayerId);
                    if (player == null || !AuthorityRuntime.ConnectionEpochFor(player.Id).Equals(command.Key.Connection)) return;
                    var header = new AuthorityEnvelopeHeader(Authority.Schema, AuthorityFamily.CommandResult,
                        Authority.Epoch, command.Key.Connection, command.Key.Sequence, GameMain.gameTick,
                        command.ConnectionPlayerId, 0);
                    player.SendPacket(AuthorityCommandResultPacket.Create(header, outcome));
                };
                var networkSink = new NetworkReplicationSink(playerId =>
                    Server?.Players?.Get(playerId) ?? Server?.Players?.Get(playerId, EConnectionStatus.Syncing));
                if (!AuthorityRuntime.RegisterHostReplication(worldView, networkSink))
                {
                    Log.Warn("[authority] host replication registration failed; the host stays fail-closed (no capture)");
                }
                else
                {
                    // A22: the vanilla death commit feeds the death ledger from the one capture
                    // point, keyed by the same adapters the replication scan reads. No replication
                    // registration means no key source either, so no capture — fail-closed.
                    AuthorityRuntime.SetHostDeathCapture(new HostDeathCapture(AuthorityRuntime.HostDeaths,
                        new VanillaDeathKeyResolver(entities, groundEnemies, spaceEnemies, crafts).TryResolveKey));
                }
            }
        }

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
