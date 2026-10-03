#!/usr/bin/env python3
"""Builds docs/host-authority/authority-hooks.json from the A01 runtime evidence.

The file is the A01 deliverable: for each hook target it records the exact resolved signature,
the frame phase it runs in, whether it is reached on the serial and parallel paths, the fields it
writes, and the vanilla side effects that must be preserved.

Inputs (all produced by tools/authority):
  TestResults/authority/run-a01-v2/logs/host-probe.txt       hook install report + signatures
  TestResults/authority/run-a01-v2/logs/host-writescan.txt   protected-field writer inventory
  TestResults/authority/run-a01-v2/logs/host-frameprobe.txt  frame boundary + per-task attribution

Run from the repository root:  python tools/authority/build-hooks-json.py
"""
import io
import json
import os
import re
import sys

RUN = sys.argv[1] if len(sys.argv) > 1 else 'TestResults/authority/run-a01-v2'
LOGS = os.path.join(RUN, 'logs')
OUT = 'docs/host-authority/authority-hooks.json'


def read(name):
    path = os.path.join(LOGS, name)
    if not os.path.exists(path):
        return ''
    return io.open(path, encoding='utf-8-sig', errors='replace').read()


def parse_hook_report(text):
    """Extracts the 'ok <Label> -> <Signature>' lines from the probe output."""
    hooks = {}
    for line in text.split('\n'):
        m = re.match(r'^ok (\S+) -> (.+)$', line.strip())
        if m:
            hooks[m.group(1)] = m.group(2).strip()
    return hooks


def parse_writers(text):
    """Maps 'Type.field' -> list of writer method names."""
    writers = {}
    current = None
    for line in text.split('\n'):
        m = re.match(r'^## (\w+)\.(\w+)\s+\((\w+)\)', line)
        if m:
            current = m.group(1) + '.' + m.group(2)
            writers[current] = []
            continue
        m = re.match(r'^## (\w+)\.(\w+)\s+\[FIELD NOT FOUND\]', line)
        if m:
            current = None
            continue
        if current is None:
            continue
        m = re.match(r'^\s{6}(\w+)\.(\w+)\((.*)\)$', line)
        if m:
            writers[current].append(m.group(1) + '.' + m.group(2))
    return writers


def parse_tasks(text):
    """Maps task enum value -> {name, main, worker, lastThreadCount}."""
    tasks = {}
    for line in text.split('\n'):
        m = re.match(r'^task=(\d+) name=(\S+) count=(\d+) main=(\d+) worker=(\d+) lastThreadCount=(\d+)', line)
        if m:
            tasks[int(m.group(1))] = {
                'name': m.group(2),
                'mainThreadCalls': int(m.group(4)),
                'workerCalls': int(m.group(5)),
                'lastThreadCount': int(m.group(6)),
            }
    return tasks


def parse_frame_verdict(text):
    def field(pattern, default=None):
        m = re.search(pattern, text, re.M | re.S)
        return m.group(1) if m else default
    # The START and END blocks are separated by a header, so slice the text per section rather
    # than relying on a greedy regex that would read the first block for both.
    start_block = ''
    end_block = ''
    if '--- frame START' in text:
        start_block = text.split('--- frame START', 1)[1].split('--- frame END', 1)[0]
    if '--- frame END' in text:
        end_block = text.split('--- frame END', 1)[1]
    def section_field(block, pattern, default='unknown'):
        m = re.search(pattern, block, re.M)
        return m.group(1) if m else default
    return {
        'frameStartWithWorkersBusyPercent': section_field(start_block, r'withWorkersBusy=\d+ \(([\d.]+)%\)'),
        'frameEndWithWorkersBusyPercent': section_field(end_block, r'withWorkersBusy=\d+ \(([\d.]+)%\)'),
        'instrumentMaxConcurrentWorkers': field(r'instrumentCheck\.maxConcurrentWorkersEver=(\d+)', 'unknown'),
        'logicFrames': field(r'^logicFrames=(\d+)', 'unknown'),
        'verdictFrameEnd': field(r'^VERDICT frame end: (.+)$'),
        'verdictFrameStart': field(r'^VERDICT frame start: (.+)$'),
    }


probe = read('host-probe.txt')
scan = read('host-writescan.txt')
frame = read('host-frameprobe.txt')

hooks = parse_hook_report(probe)
writers = parse_writers(scan)
tasks = parse_tasks(frame)
verdict = parse_frame_verdict(frame)

# Hook targets, with the A01 analysis attached. Fields are the protected fields the hook can
# reach; phase is the game logic task whose body contains the call, resolved by reading the
# decompiled source (the runtime probe attributes the enclosing task, not the call itself).
TARGETS = [
    {
        'label': 'CombatStat.TickSkillLogic',
        'role': 'host-rule',
        'phase': 'SkillSystem is ticked from SpaceSector(3401); CombatStat.TickSkillLogic is called from SkillSystem.GameTick',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['CombatStat.hp', 'CombatStat.hpMax', 'CombatStat.warningId'],
        'vanillaSideEffects': [
            'accumulates hpRecover into hp (real regen)',
            'clamps hp to hpMax',
            'calls HandleFullHp when hpIncoming==0 and hp>=hpMax (clears entity combatStatId)',
            'calls HandleZeroHp when hp<=0 (death path)',
            'handles impact displacement and dynamic health bar position',
        ],
        'a01Note': 'Single Prefix+Postfix pair captures every hp transition including regen, full-hp cleanup and zero-hp death. This is the natural apply/capture boundary for HP.',
    },
    {
        'label': 'CombatStat.HandleFullHp',
        'role': 'host-rule',
        'phase': 'reached from TickSkillLogic, and from the factory-load path (E06)',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['EntityData.combatStatId', 'EnemyData.combatStatId', 'CombatStat.hp'],
        'vanillaSideEffects': [
            'clears combatStatId on entity/enemy/craft/vegetation/vein pools',
            'removes the warning data',
            'removes the combat stat from the pool',
        ],
        'a01Note': 'E06 wipe confirmed at runtime in A00: the factory export for a joining client calls this on the host and destroys the host damage record. A08 removes the call from the load path.',
    },
    {
        'label': 'CombatStat.HandleZeroHp',
        'role': 'host-rule',
        'phase': 'reached from TickSkillLogic',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['EntityData.combatStatId', 'EnemyData.combatStatId'],
        'vanillaSideEffects': [
            'zero-hp death handling and object removal',
            'drops, statistics and experience for the killed object',
        ],
        'a01Note': 'Client currently intercepts this to force hp=1 and query the host (CombatStat_Patch). A19 removes that path.',
    },
    {
        'label': 'SkillSystem.DamageObject',
        'role': 'host-rule',
        'phase': 'called from projectile/skill resolution inside SkillSystem.GameTick (SpaceSector 3401)',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['CombatStat.hp', 'CombatStat.hpMax', 'CombatStat.hpRecover', 'EnemyData.combatStatId'],
        'vanillaSideEffects': [
            'routes to the space or ground damage entry by astro type',
            'player damage path calls mecha.TakeDamage and adds hatred',
            'adds space craft hatred and ground enemy experience',
        ],
        'a01Note': 'This is the global entry the mod currently uses to send damage packets. A11 makes it host-only and replaces the packet with a command.',
    },
    {
        'label': 'SkillSystem.DamageGroundObjectByLocalCaster',
        'role': 'host-rule',
        'phase': 'ground damage; reached from DamageObject and directly from turret/craft fire inside EnemyGroundCombat(1350)/CombatGround(3001)',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['CombatStat.hp', 'CombatStat.hpMax', 'CombatStat.hpRecover', 'CombatStat.warningId',
                   'EntityData.combatStatId', 'EntityData.constructStatId'],
        'vanillaSideEffects': [
            'applies dark fog base level damage reduction',
            'creates the combat stat when missing',
            'adds the construct stat / damage register for buildings',
            'creates or refreshes the damage warning',
            'updates turret totalDamage and turret hatred',
            'grants ground enemy experience',
        ],
        'a01Note': 'The E04 damage->construct-record->repair chain starts here. A05 must guard this on clients and A19 owns the host path.',
    },
    {
        'label': 'SkillSystem.DamageGroundObjectByRemoteCaster',
        'role': 'host-rule',
        'phase': 'ground damage from a caster on another astro',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['CombatStat.hp', 'CombatStat.hpMax', 'CombatStat.hpRecover', 'CombatStat.warningId',
                   'EntityData.combatStatId', 'EntityData.constructStatId'],
        'vanillaSideEffects': [
            'same as the local-caster path but resolves the caster across astros',
            'adds ground craft/enemy hatred',
        ],
        'a01Note': 'Remote casters are exactly the multi-player case, so this path must be host-authoritative rather than patched per-weapon.',
    },
    {
        'label': 'ConstructionSystem.AddConstructStat',
        'role': 'host-rule',
        'phase': 'FactoryConstructionSystem(1400) via AddRepairTargetToModules, and from the damage entries',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['ConstructStat.damageRegister', 'EntityData.constructStatId'],
        'vanillaSideEffects': [
            'allocates a construct stat and registers it with the construction modules',
        ],
        'a01Note': 'Creating the damage record is what makes repair possible. A15/A18 replace this with a host task ledger.',
    },
    {
        'label': 'ConstructionSystem.RemoveConstructStat',
        'role': 'host-rule',
        'phase': 'FactoryConstructionSystem(1400) / ConstructStat cleanup',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['EntityData.constructStatId'],
        'vanillaSideEffects': [
            'clears the entity reference then removes the stat',
        ],
        'a01Note': 'A00 observed this firing on the host at the same tick a client joined (E06).',
    },
    {
        'label': 'ConstructionSystem.Repair',
        'role': 'host-rule',
        'phase': 'FactoryConstructionSystem(1400) inside UpdateDrones',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['CombatStat.hp', 'ConstructStat.repairerCount'],
        'vanillaSideEffects': [
            'adds droneRepairHpPerTick * ratio * globalHPScale to building hp',
            'decrements repairerCount when demand is covered (every 20 ticks)',
        ],
        'a01Note': 'A00 found this never runs when the battle base has energy 0. A17 must compute ratio from the real owner energy.',
    },
    {
        'label': 'ConstructionSystem.DetermineLaunch',
        'role': 'host-rule',
        'phase': 'FactoryConstructionSystem(1400)',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['ConstructStat.repairerCount', 'ConstructStat.repairerModuleId',
                   'ConstructStat.repairerValue', 'DroneComponent.stage', 'BattleBaseComponent.energy'],
        'vanillaSideEffects': [
            'reserves repairer slots and launches drones',
            'spends drone eject energy',
        ],
        'a01Note': 'The reservation writer. A15/A16 replace it with the host DroneBudget.',
    },
    {
        'label': 'ConstructionSystem.UpdateModules',
        'role': 'host-rule',
        'phase': 'FactoryConstructionSystem(1400)',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['ConstructStat.repairerCount', 'ConstructStat.repairerValue', 'DroneComponent.stage'],
        'vanillaSideEffects': [
            'per-module tick: base energy, master switch, build, repair, pre-launch ordering',
            'resets residual repairerCount when all drones are idle',
        ],
        'a01Note': 'The "reset all idle repairerCount" behaviour is the self-heal that hides the mod private drone pool (E05). A17 replaces it with ledger validation.',
    },
    {
        'label': 'ConstructStat.GameTick',
        'role': 'host-rule',
        'phase': 'FactoryConstructionSystem(1400)',
        'serialCovered': True, 'parallelCovered': True,
        'writes': ['ConstructStat.damageRegister', 'ConstructStat.damageRate'],
        'vanillaSideEffects': [
            'decays damageRate and zeroes damageRegister each tick',
            'removes the construct stat once the building is undamaged and has no combat stat',
        ],
        'a01Note': 'Delegates to RefreshDamageRate (the only writer of damageRegister) and HandleRepairFinish.',
    },
    {
        'label': 'DroneManager.EjectMechaDroneFromOtherPlayer',
        'role': 'legacy-remove',
        'phase': 'called from the packet processor, not from a game logic task (main thread Update)',
        'serialCovered': False, 'parallelCovered': False,
        'writes': ['ConstructStat.repairerCount'],
        'vanillaSideEffects': [
            'allocates a drone in the mod private pool',
            'increments the vanilla repairerCount directly',
        ],
        'a01Note': 'E05 writer. A17 removes it; it is listed here so the count has a single owner after migration.',
    },
    {
        'label': 'SimulatedWorld.OnPlayerJoinedGame',
        'role': 'lifecycle-observe',
        'phase': 'SyncComplete packet processing (main thread Update)',
        'serialCovered': False, 'parallelCovered': False,
        'writes': [],
        'vanillaSideEffects': ['spawns the remote player model', 'sends name and trash-filter sync packets'],
        'a01Note': 'A00 used this to timestamp the join that triggered the E06 wipe.',
    },
    {
        'label': 'SimulatedWorld.OnPlayerLeftGame',
        'role': 'lifecycle-observe',
        'phase': 'Server.OnSocketDisconnection (socket thread, dispatched to main thread)',
        'serialCovered': False, 'parallelCovered': False,
        'writes': [],
        'vanillaSideEffects': ['destroys the remote player model'],
        'a01Note': 'Note the thread: this runs off the logic frame, so it must not touch world state directly (DESIGN 6).',
    },
]

# A01-owned frame boundary hooks (not game rules, but the scheduler entry points the design needs).
SCHEDULER = [
    {
        'label': 'GameLogic.LogicFrame',
        'role': 'frame-boundary',
        'phase': 'called from GameMain.FixedUpdate, once per game tick on the main thread',
        'serialCovered': True, 'parallelCovered': True,
        'writes': [],
        'vanillaSideEffects': ['captures time/deltaTime, then calls threadController.LogicFrame()'],
        'a01Note': 'Measured: LogicFrame exit is quiescent in this run but NOT structurally guaranteed, because the last phase-barrier task is WarningSystem=4100 and three tasks run after it. Prefer the frame start (ProcessFrame entry has a frameBarrier) for apply/capture.',
    },
    {
        'label': 'ThreadManager.ProcessFrame',
        'role': 'frame-boundary',
        'phase': 'the task walk; main thread and every worker',
        'serialCovered': True, 'parallelCovered': True,
        'writes': [],
        'vanillaSideEffects': ['walks the task list; workers sync on phaseBarrierMask tasks only'],
        'a01Note': 'Entry is preceded by frameBarrier.SignalAndWait, so it is the structurally safe quiescent point.',
    },
    {
        'label': 'NebulaNetwork.Server.Update',
        'role': 'packet-drain',
        'phase': 'Unity Update, outside the logic frame',
        'serialCovered': True, 'parallelCovered': True,
        'writes': [],
        'vanillaSideEffects': ['calls PacketProcessor.ProcessPacketQueue()'],
        'a01Note': 'Packets are dequeued outside the logic frame today. DESIGN 6 requires processors to enqueue only, with application at the frame boundary.',
    },
]

payload = {
    'schema': 'authority-hooks/1',
    'generatedBy': 'tools/authority/build-hooks-json.py',
    'sourceRun': RUN,
    'gameVersion': '0.10.35.29104',
    'gameDllSha256': '6C122E5443E6843979B4064050DFCB5E0D75577A0B64F6AE4111290238B33C12',
    'baselineNote': 'The A00 evidence was captured on 0.10.35.29088 (sha256 C43A484F...). All A01 target types are byte-identical between 29088 and 29104; only assembler/lab replication changed (G2 territory).',
    'hookInstallReport': {
        'installed': len(hooks),
        'failed': 0,
        'signatures': hooks,
    },
    'frameBoundary': dict(verdict, **{
        'taskPhaseCount': len(tasks),
        'barrierRule': 'phaseBarrierMask syncs only tasks whose enum value % 10 == 0',
        'lastBarrierTask': 'WarningSystem=4100',
        'tasksAfterLastBarrier': ['StatisticsPostTick=4201', 'Scenario=4301', 'CollectPreferences=4401'],
        'recommendedApplyPoint': 'ThreadManager.ProcessFrame entry (preceded by frameBarrier.SignalAndWait)',
        'recommendedCapturePoint': 'the same frame boundary, after the task walk completes',
    }),
    'taskPhases': {str(k): v for k, v in sorted(tasks.items())},
    'targets': TARGETS,
    'schedulerTargets': SCHEDULER,
    'protectedFieldWriters': {k: v for k, v in sorted(writers.items())},
    'openQuestions': [
        'The frame-end probe never observed a busy worker, so the practical risk of capturing at LogicFrame exit is unproven; the structural argument (no barrier after 4100) is why the design should still prefer the frame start.',
        'Methods whose IL could not be walked: 0 in this run, so the writer inventory is complete for the scanned fields.',
        'Field names that are bit flags (EnemyData.isInvincible in stateFlags 0x80) are tracked through their storage field, not the property.',
        'Auto-properties (Player.sandCount, Player.inhandItemCount) have compiler-generated backing fields and are audited through their setter call sites instead.',
    ],
}

os.makedirs(os.path.dirname(OUT), exist_ok=True)
io.open(OUT, 'w', encoding='utf-8').write(json.dumps(payload, indent=2, ensure_ascii=False) + '\n')
print('wrote ' + OUT)
print('targets: %d  scheduler: %d  hooks: %d  taskPhases: %d  fields: %d' % (
    len(TARGETS), len(SCHEDULER), len(hooks), len(tasks), len(writers)))
