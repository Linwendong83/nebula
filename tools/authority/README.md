# tools/authority — A00 baseline harness

Committed diagnostic tooling for the host-authority work package in `docs/host-authority/`.
It exists so the next implementer does not need the untracked local `scripts/RestorationSmoke`
driver, and so every baseline claim has a reproducible command behind it.

**Scope: observation only.** Nothing in `AuthorityBaseline` changes hp, repair counts, repairer
slots, resources, task state, spawns or lifecycle. The single exception is the scenario driver's
explicit fault injection (`attack`, `damagebuilding`), and every injected event is written to the
log as a `driver.inject` record so it can never be mistaken for organic gameplay.

## Layout

| Path | Purpose |
| --- | --- |
| `AuthorityBaseline/` | BepInEx plugin: read-only hooks + frame sampler + scenario driver |
| `run-authority-instance.ps1` | Launches one isolated game instance (`host`, `client1`, `client2`) |
| `invoke-authority-scenario.ps1` | Drives the three A00 scenarios and collects evidence |
| `invoke-authority-matrix.ps1` | A22/A23: drives the forced C/R/L x N matrix cases (66 today) |
| `invoke-authority-perf.ps1` | A23: measures capture/apply cost and per-family traffic on a live pair |
| `invoke-authority-gate.ps1` | A25: collects the G1 release-gate evidence; unmeasured items stay null so the gate blocks |
| `README.md` | This file |

The G1 gate's decision itself is code, not prose: `NebulaModel/Authority/AuthorityReleaseGate.cs`,
frozen against the live build by `NebulaModel/Authority/AuthorityReleaseContract.cs`, with the release
notes in `docs/host-authority/RELEASE-G1.md`.

Build output and all evidence go to the git-ignored `TestResults/authority/`.

## Isolation guarantees

Each instance runs from `TestResults/authority/<run>/instances/<role>/`:

- The game payload is **junctioned**, not copied, and the instance directory is the only writable
  part, so no run can modify the real installation.
- The local `dsp-steamless` plugin is copied into every instance, so a client can boot while
  the shared Steam library is in use by someone else (`SteamAPI_Init` failure makes UIRunner
  quit the process). The real installation is untouched; the dedicated host never reaches
  SteamAPI and does not need it.
- The save profile is per instance and validated to stay inside the instance directory; a bad
  `Configs/path.txt` is rejected rather than followed. The real player profile is never used.
- Each instance gets its own player key (`authority-player.key`), so three instances on one
  machine are three players instead of one identity reconnecting.
- Port, UPnP, Discord RPC and remote access are disabled in the instance config.

## Usage

```powershell
# Host: combat-mode sandbox so dark fog exists to observe.
pwsh tools/authority/run-authority-instance.ps1 -Role host -Build -RunId run-a00 -Combat -FreshProfile

# Two clients (they retry until the host accepts them)
pwsh tools/authority/run-authority-instance.ps1 -Role client1 -RunId run-a00
pwsh tools/authority/run-authority-instance.ps1 -Role client2 -RunId run-a00

# Drive the scenarios (host + clients must be ready first)
pwsh tools/authority/invoke-authority-scenario.ps1 -Scenario sustained-attack -RunId run-a00
pwsh tools/authority/invoke-authority-scenario.ps1 -Scenario repair-dispatch -RunId run-a00
pwsh tools/authority/invoke-authority-scenario.ps1 -Scenario third-player -RunId run-a00
```

`-Combat` is required for scenarios 1 and 2: a default sandbox is peace mode with no dark fog and
no buildings, so there is nothing to observe.

Stop an instance by writing `quit` to its command file:

```powershell
'x quit' | Set-Content TestResults/authority/run-a00/control/host.command
```

## Instance prerequisites

Four non-obvious conditions are handled by the driver. They cost real debugging time and should
not be re-derived:

1. **Wait for host tick progress, not just `IsGameLoaded`.** A dedicated host can report loaded
   while its logic frame is still paused (`isFullscreenPaused=True`); clients are then rejected
   with `HostStillLoading`. `Wait-Ready -RequireProgress` requires an observed tick increase.
2. **Do not connect before `GameMain.data` exists.** `ClientSocket_OnOpen` sends `LobbyRequest`
   reading `GameMain.data.account.userName`; connecting too early throws and the socket open
   never completes.
3. **Stop retrying once `Multiplayer.IsActive`.** `Multiplayer.JoinGame` calls `DSPGame.EndGame()`
   when a world is already loaded, so a retry after the host accepted us tears down the world.
4. **Answer the goal-level picker.** A first-time client is shown the goal picker and stalls in
   loading until it is answered; the driver selects `EGoalLevel.Key` through the same
   `GoalManager.SelectInitialLevel` path the UI handler uses.

## Control protocol

The driver polls `<run>/control/<role>.command` once per frame and reacts only when the content
changes. The first word is ignored (it is a unique token so repeated commands still register);
the verb is the second word.

| Command | Effect |
| --- | --- |
| `status` | Refresh the status file |
| `observe enemy [planetId] [enemyId]` | Start tracking an enemy (auto-picks one when omitted) |
| `observe building [planetId] [entityId]` | Start tracking a building (prefers a battle base) |
| `attack <count> <damage> <intervalTicks> [playerCaster]` | Inject `<count>` damage events on the tracked enemy |
| `damagebuilding <count> <damage> <intervalTicks> [playerCaster]` | Same, on the tracked building |
| `setupbase` | Place one battle base next to the host mecha through the vanilla entity path |
| `frameprobe start <n>` / `frameprobe dump` | A01: arm the ordered structural event log for n frames, then write the frame-boundary report |
| `writescan` | A01: write `<role>-writescan.txt` (protected-field writer inventory from real IL) |
| `scan` | Write `<role>-scan.txt`: per-planet enemy/building counts plus dark fog occupancy |
| `sample` | Force one sampler pass |
| `sweep` | Write `<role>-sweep.txt` (per-enemy hp min/max/increase/zero counts) |
| `probe` | Write `<role>-probe.txt` (identity, repair eligibility, hook report, transcript) |
| `disconnect` / `reconnect` | Drop the session and let the driver's join loop run again (fresh factory load) |
| `fault <spec>` | A22 matrix: re-arm the runtime fault rules from a spec string (same refuse-the-whole-spec semantics as the former `-nebula-authority-faults` flag); logged as `driver.fault`. Release: the fault link is always wired, so no launch flag is needed. |
| `faultclear` | Zero the wired link's rules in place (stays attached so later `fault` still reaches it) |
| `faultstatus` | Report `enabled=<link wired>` and `armed=<fault active>` plus the current rules |
| `replica` | A22 matrix diagnostic: dump the authority replica/replicator state (subscription phases, baselines, stream positions, counters) to `<role>-replica.txt`; read-only |
| `perf` | A23 diagnostic: write `<role>-perf.txt` — capture/apply latency p50/p95, per-family byte rates, queue depths and the budget in force; read-only |
| `clear` | Drop all tracked targets |
| `quit` | Exit the game |

`setupbase` and the two damage commands are harness setup for a throwaway sandbox, not mod
behaviour: they go through vanilla entry points and every effect is logged as `driver.setup` or
`driver.inject` so injected state can never be read as organic gameplay.

Status is written to `<run>/control/<role>.status` every second. `command=` echoes the exact
line the driver consumed, which is what the PowerShell helper waits on.

## Log schema

One JSON record per line in `<run>/logs/<role>.jsonl`. Field set is the A00 minimum:

`seq, runId, role, isServer, tick, thread, threadKind, event, source, planetId, objectKey,
rawSlot, hpBefore, hpAfter, hpMax, hpRecover, hpIncoming, repairerBefore, repairerAfter, owner,
command, transaction, revision, reason, note`

`objectKey` follows the DESIGN 4.1 shape but writes the authority epoch as `-`, and `command`
and `transaction` are always `null`. That is deliberate: A02/A03 have not built those layers
yet, and the evidence should show the gap rather than a fabricated identity.

`hpBefore`/`repairerBefore` come from the hook's own thread-local snapshot taken in the matching
prefix, not from a previous frame's sample, so concurrent worker writes cannot make them lie.

Event families:

| Event | Meaning |
| --- | --- |
| `hp.tick` | hp changed inside `CombatStat.TickSkillLogic` (covers regen, zero-hp, full-hp) |
| `hp.fullhp.begin/end` | `HandleFullHp` ran — the E06 join/load path that clears combat state |
| `hp.zerohp.begin/end` | `HandleZeroHp` ran |
| `damage.entry` | `SkillSystem.DamageObject` (the space/global entry) |
| `damage.ground.local` / `damage.ground.remote` | the two ground damage entries (E04) |
| `construct.add` / `construct.remove` | `ConstructStat` lifecycle |
| `repair.tick` | `ConstructionSystem.Repair` — hp delta and energy ratio |
| `repair.launch` | a drone changed stage/target in `DetermineLaunch` |
| `repair.count` | `repairerCount` changed (from `DetermineLaunch`, `UpdateModules`, `ConstructStat.GameTick`) |
| `drone.remote.eject` | `DroneManager.EjectMechaDroneFromOtherPlayer` — the E05 remote path |
| `session.join` / `session.leave` / `session.roster` | who is connected, on which planet |
| `sample.change` / `sample.heartbeat` | frame-side sampler; the backstop for changes no hook covers |
| `driver.inject` | an injected scenario damage event (never organic gameplay) |

## Hook health

`BaselineHooks.Install()` records every patch it installs or fails to install. A missing target
method is a `FAIL` line in the `probe` output and increments `hookFailures`, which is also
surfaced in the status file and in `<role>-manifest.json`. Hooks never return `false` and never
assign to game fields, so a broken probe degrades the evidence, not the game.

## A01 additions

- `FrameProbe` (in the plugin) measures whether the frame boundary is quiescent, and attributes
  every game logic task to the thread ordinals that executed it. Its result is reported with an
  instrument self-check (`maxConcurrentWorkersEver` must reach the worker count, otherwise the
  busy counter itself is broken and the 0% reading would be meaningless).
- `WriteScanner` (in the plugin) enumerates, from real IL, every method that stores into a
  protected field. It self-tests against three hand-verified writer anchors and prints the
  resolved opcode values, so a misconfigured scan fails loudly instead of reporting "no writers".
- `build-hooks-json.py` turns those runtime outputs into `docs/host-authority/authority-hooks.json`.
- The local-only `NebulaTests/Authority/AuthorityHookInventoryTest.cs` (excluded from Git and
  the solution) re-derives the inventory from IL in both
  directions and fails if the JSON is stale, missing, or padded with writers that do not exist.

### A01 caveats worth remembering

- `stfld` is `0x7D` and `stsfld` is `0x80`. Two earlier revisions of the scanner hardcoded wrong
  values and reported zero writers for every field; the values are now resolved by name at runtime.
- `OpCodes` exposes PascalCase fields (`Stfld`), while IL mnemonics are lowercase (`stfld`). The
  lookup is case-insensitive for that reason.
- Some protected "fields" are not fields: `EnemyData.isInvincible` is bit `0x80` of `stateFlags`,
  and `ConstructionModuleComponent.droneCount` is a property over `_droneCount`. They are tracked
  through their real storage.
- A writer can live in an unrelated type: `GameHistoryData.UnlockTechFunction` writes `Mecha.hp`
  when the hp-upgrade tech is unlocked. Any scan limited to the field's declaring type misses it.
- Methods containing a `switch` use `InlineSwitch`, whose operand length is variable. A walker that
  does not model it aborts early and silently hides that method's writers.

## Known gaps (carried into A01+)

- `command`, `transaction` and the real authority epoch do not exist in the runtime yet. A02 built
  the pure model for them (`NebulaModel/Authority/`), but nothing mints an epoch or installs a
  command window until A03/A04, so the baseline log still writes `epoch=-`.
- No revision counter is wired to the game yet; `revision` carries the existing damage-generation
  value. A02 added `ObjectRevisionTracker` for the host side but it is not connected.
- The sampler's frame phase is `Update`, not a proven end-of-rules barrier. A01 must place
  capture after the worker barrier before any of this becomes load-bearing for correctness.
- `repair.launch` covers `DetermineLaunch` only. Other dispatch paths (module pre-launch, the
  Nebula remote drone pool) are logged where they are observable but are not yet attributed to a
  single owner key.
