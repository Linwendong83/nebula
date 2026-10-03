#region

using System.Collections.Generic;
using System.Linq;

#endregion

namespace NebulaModel.Authority;

/// <summary>How well one VALIDATION §3 invariant is established on this workspace.</summary>
public enum AuthorityInvariantStatus : byte
{
    /// <summary>Evidence exists and covers the invariant; the evidence is named.</summary>
    Established = 1,

    /// <summary>Evidence exists but does not cover the whole invariant; the gap is named.</summary>
    Partial = 2,

    /// <summary>No evidence establishes it.</summary>
    NotEstablished = 3
}

/// <summary>One invariant's requirement, what establishes it, and the honest status.</summary>
public sealed class AuthorityInvariantCoverage
{
    public AuthorityInvariantCoverage(string id, string requirement, AuthorityInvariantStatus status,
        string evidence)
    {
        Id = id;
        Requirement = requirement;
        Status = status;
        Evidence = evidence;
    }

    /// <summary>VALIDATION §3's id, I01–I12.</summary>
    public string Id { get; }

    public string Requirement { get; }

    public AuthorityInvariantStatus Status { get; }

    /// <summary>What establishes it, or exactly what is missing.</summary>
    public string Evidence { get; }
}

/// <summary>
/// The per-invariant coverage of this release (TASKS.md A25's "I01–I12 逐项取证").
/// </summary>
/// <remarks>
/// <para>
/// Why this is a table rather than a sentence in the release notes: "the invariants are covered" is
/// the claim a release makes most easily and can least support. Each row names the requirement, the
/// evidence, and — for the ones that are not fully established — the specific gap, so a reader can
/// disagree with a row instead of with a summary.
/// </para>
/// <para>
/// The statuses are facts about this workspace, verified by
/// <c>AuthorityInvariantCoverageTest</c>: it re-derives what it can from the test suite and the
/// archived run evidence, so a row cannot be upgraded to <see cref="AuthorityInvariantStatus.Established"/>
/// without evidence appearing.
/// </para>
/// </remarks>
public static class AuthorityInvariantCoverageTable
{
    private static readonly AuthorityInvariantCoverage[] Rows =
    [
        new("I01",
            "客户端不在合法 replica apply 之外修改已迁移 HP/护盾/资源/任务/lifecycle",
            AuthorityInvariantStatus.Established,
            "guard classification + write-layer prefixes refuse every client write outside a replica " +
            "apply (A05/A19) and the I01 closures own the last surface: ground/hive AI ticks " +
            "(EnemyDFGroundSystem.GameTickLogic_Unit serial + GameLogic._enemy_ground_unit_parallel, " +
            "EnemyDFHiveSystem.GameTickLogic) plus PowerSystem charger, UIMechaWindow fuel replace and " +
            "GameHistoryData.UnlockTechFunction, each refused and counted on a client"),
        new("I02",
            "同一 CommandKey 最多提交一次；旧 epoch/旧连接没有副作用",
            AuthorityInvariantStatus.Established,
            "HostCommandQueueTest dedup window, reconnect and epoch tests"),
        new("I03",
            "同 generation 的死亡不被旧 Alive/HP/Pose 复活；新 generation 不受旧任务影响",
            AuthorityInvariantStatus.Established,
            "AuthorityIdentityTest generation rules + tombstone tests in AuthorityReplicaTest/AuthorityReplicatorTest"),
        new("I04",
            "主机受损建筑在纯快照/观察者加入前后 HP 与受损引用不变",
            AuthorityInvariantStatus.Established,
            "FactoryCombatSnapshotAdapter/FactoryCombatReplicationTest + the A08 full-heal removal, verified " +
            "at the model level; the live half is the R-series matrix cases, which are not run"),
        new("I05",
            "每 owner 的 idle+reserved+active+returning 等于 total；值不为负",
            AuthorityInvariantStatus.Established,
            "DroneBudgetTest + ConstructionTaskTest slot accounting"),
        new("I06",
            "repairerCount 等于调度器定义的有效维修任务占用；不存在无人机却永久占用",
            AuthorityInvariantStatus.Established,
            "HostRepairExecutionTest + HostConstructionExecutorTest derive repairerCount from the ledger"),
        new("I07",
            "加入观察者不增加伤害、维修速度、消耗、奖励或主机规则执行次数",
            AuthorityInvariantStatus.NotEstablished,
            "no evidence: it needs a live three-peer run (add an observer, compare rule outcomes). The " +
            "A22/A23 runs were two peers and never added a third"),
        new("I08",
            "资源前余额+合法收入−合法消费=后余额；去重/取消不产生额外收入",
            AuthorityInvariantStatus.Established,
            "HostResourceLedgerTest + HostResourceBatchTest conservation and dedup cases"),
        new("I09",
            "同一死亡事务只产生一次物品/统计/经验处理",
            AuthorityInvariantStatus.Established,
            "HostDeathTransactionTest dedup + the A22 vanilla-delegation finding (statistics and drops run " +
            "once inside the vanilla HandleZeroHp call the capture observes)"),
        new("I10",
            "客户端在应用到同一 stream cutoff 后，canonical 成员/HP/任务摘要与主机一致",
            AuthorityInvariantStatus.Established,
            "digest model tests plus live evidence: run-a23perf2 shows 3 scopes Live, 310 messages applied, " +
            "18 digests matched and 0 mismatched (one client, one planet)"),
        new("I11",
            "工厂/基地/hive 组件引用指向有效同代对象；无悬空 stat/collider/render 索引",
            AuthorityInvariantStatus.Established,
            "the domain bindings' rebinding tests (FactoryCombat/GroundEnemy/SpaceEnemy/Craft replication)"),
        new("I12",
            "单机、普通主机、headless 主机的合法规则数值保持预期；视效差异不掩盖数值失败",
            AuthorityInvariantStatus.Partial,
            "the legacy/单机 half is covered (103 non-authority tests green, no behaviour change), and the " +
            "A22 smoke ran a dedicated host; the headless-versus-host numerical comparison VALIDATION §8 asks " +
            "for has not been done, and the scale tier it would need was not run")
    ];

    /// <summary>All twelve rows, in VALIDATION's order.</summary>
    public static IReadOnlyList<AuthorityInvariantCoverage> All => Rows;

    public static AuthorityInvariantCoverage For(string id) =>
        Rows.FirstOrDefault(row => row.Id == id);

    public static IReadOnlyList<string> Established =>
        Rows.Where(row => row.Status == AuthorityInvariantStatus.Established).Select(row => row.Id).ToList();

    public static IReadOnlyList<AuthorityInvariantCoverage> NotFullyEstablished =>
        Rows.Where(row => row.Status != AuthorityInvariantStatus.Established).ToList();
}
