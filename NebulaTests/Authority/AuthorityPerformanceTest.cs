#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A23: the performance meter reports latency distributions, per-family traffic and queue depths,
/// and its own footprint stays bounded however long the session runs.
/// </summary>
/// <remarks>
/// The numbers are checked against hand-computed expectations rather than "whatever the code
/// printed", because the meter is what an A23 budget claim will be judged against: a percentile that
/// is computed from the wrong window, or a byte rate divided by wall clock instead of host ticks,
/// would produce a plausible-looking report that no one can compare across machines.
/// </remarks>
[TestClass]
public class AuthorityPerformanceTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A0A0A0A0A0A0A0A, 0x0A0A0A0A0A0A0A0A);

    [TestMethod]
    public void TheApplyPassIsMeasuredEvenWhenTheQueueDrains()
    {
        // Regression: a healthy client drains its apply queue to empty every frame. A pass that
        // returned early on "queue is empty" would record no apply cost at all, so a real run would
        // report p95=0 and look perfect while measuring nothing.
        var session = new AuthoritySession(new AuthoritySessionState());
        session.Identity.OnPeerNegotiated(AuthorityMode.HostAuthority);
        session.BeginAuthorityWorld(Epoch, isHost: false);

        var applier = new CountingApplier();
        session.ReplicaApplier = applier;
        session.TryEnqueueReplicaMessage(StatePacket(AuthorityFamily.WorldState, 300),
            new ApplyScope(new ScopeKey(PoolKind.GroundEnemy, 101)));

        session.OnFrameBoundary(500);
        session.OnFrameBoundary(501);

        TestAssert.AreEqual(1, applier.Applied);
        TestAssert.AreEqual(2L, session.Metrics.ApplyFrames,
            "Both frames' apply passes are reported, including the one that found the queue empty.");
        TestAssert.AreEqual(1L, session.Metrics.ApplyMessages);
        TestAssert.IsTrue(session.Metrics.ApplyBytes >= 300, "The applied bytes are accounted.");
        TestAssert.IsTrue(session.Metrics.ApplyLatency.Count == 2);
        TestAssert.AreEqual(1L, session.Metrics.ElapsedTicks, "Two frame ticks, one tick apart.");
        TestAssert.AreEqual(1L, session.Metrics.PacketsReceivedFor(AuthorityFamily.WorldState));
    }

    private sealed class CountingApplier : IReplicaMessageApplier
    {
        public int Applied { get; private set; }

        public bool Apply(in PendingReplicaMessage message)
        {
            Applied++;
            return true;
        }
    }

    private static AuthorityWorldStatePacket StatePacket(AuthorityFamily family, int payloadBytes) =>
        AuthorityWorldStatePacket.Create(
            new AuthorityEnvelopeHeader(AuthoritySchema.V1, family, Epoch, connection: default, sequence: 1,
                hostTick: 500, claimedPlayerId: 0, payloadLength: 0),
            new ScopeKey(PoolKind.GroundEnemy, 101), declaredBaselineId: 1, recordCount: 0,
            data: new byte[payloadBytes]);

    [TestMethod]
    public void SummariesAreEmptyBeforeAnySample()
    {
        var meter = new AuthorityPerfMeter("host", windowSize: 8);

        TestAssert.AreEqual(0, meter.CaptureLatency.Count);
        TestAssert.AreEqual(0, meter.ApplyLatency.Count);
        TestAssert.AreEqual(0.0, meter.CaptureLatency.P95Ms);
        TestAssert.AreEqual(0.0, meter.SentBytesPerSecond);
        TestAssert.AreEqual(0L, meter.ElapsedTicks);
    }

    [TestMethod]
    public void PercentilesComeFromTheSortedWindow()
    {
        var meter = new AuthorityPerfMeter("host", ticksPerSecond: 60, windowSize: 10);

        // 1..10 ms. p50 is the ceiling(0.5*10)=5th sample, p95 the ceiling(0.95*10)=10th.
        for (var i = 1; i <= 10; i++) meter.RecordHostCapture(100 + i, i, packets: 1, bytes: 1, 0, 0, 0);

        var summary = meter.CaptureLatency;
        TestAssert.AreEqual(10, summary.Count);
        TestAssert.AreEqual(5.0, summary.P50Ms);
        TestAssert.AreEqual(10.0, summary.P95Ms);
        TestAssert.AreEqual(10.0, summary.MaxMs);
        TestAssert.AreEqual(5.5, summary.MeanMs);
        TestAssert.AreEqual(10.0, meter.PeakCaptureMs);
    }

    [TestMethod]
    public void TheWindowIsBoundedSoALongRunCannotGrowTheMeter()
    {
        var meter = new AuthorityPerfMeter("host", windowSize: 16);

        for (var i = 0; i < 10_000; i++) meter.RecordHostCapture(1000 + i, i % 7, 1, 1, i, i, 0);
        for (var i = 0; i < 10_000; i++) meter.RecordClientApply(2000 + i, i % 5, 1, 1, 0, false);

        TestAssert.AreEqual(16, meter.CaptureLatency.Count, "The sample window is fixed size.");
        TestAssert.AreEqual(16, meter.ApplyLatency.Count, "The sample window is fixed size.");
        TestAssert.AreEqual(10_000, meter.CaptureFrames);
        TestAssert.AreEqual(10_000, meter.ApplyFrames);
        // The retained window holds the most recent samples, not the first ones.
        TestAssert.AreEqual(6.0, meter.CaptureLatency.MaxMs, "0..6 is the retained tail of i%7.");
    }

    [TestMethod]
    public void BytesPerSecondComesFromHostTicksNotFromWallClock()
    {
        var meter = new AuthorityPerfMeter("client", ticksPerSecond: 60, windowSize: 8);

        // Two sampled host ticks 600 apart at 60 ticks/s is exactly 10 seconds; 200 received bytes
        // over that span is 20 bytes/s whatever the wall clock did.
        meter.RecordClientApply(0, 0.1, 1, 100, 0, false);
        meter.RecordPacketReceived(AuthorityFamily.WorldState, 100);
        meter.RecordClientApply(600, 0.1, 1, 100, 0, false);
        meter.RecordPacketReceived(AuthorityFamily.WorldState, 100);

        TestAssert.AreEqual(600L, meter.ElapsedTicks);
        TestAssert.AreEqual(200, meter.BytesReceived);
        TestAssert.AreEqual(20.0, meter.ReceivedBytesPerSecond, 0.001, "200 bytes over 10 seconds.");
    }

    [TestMethod]
    public void PerFamilyCountersAndRatesAreKeptApart()
    {
        var meter = new AuthorityPerfMeter("host", ticksPerSecond: 60, windowSize: 8);
        meter.RecordHostCapture(0, 0.1, 0, 0, 0, 0, 0);
        meter.RecordHostCapture(600, 0.1, 0, 0, 0, 0, 0);

        meter.RecordPacketSent(AuthorityFamily.WorldState, 1000);
        meter.RecordPacketSent(AuthorityFamily.WorldState, 500);
        meter.RecordPacketSent(AuthorityFamily.Lifecycle, 100);
        meter.RecordPacketReceived(AuthorityFamily.Command, 40);

        TestAssert.AreEqual(2, meter.PacketsSentFor(AuthorityFamily.WorldState));
        TestAssert.AreEqual(1500, meter.BytesSentFor(AuthorityFamily.WorldState));
        TestAssert.AreEqual(100, meter.BytesSentFor(AuthorityFamily.Lifecycle));
        TestAssert.AreEqual(1, meter.PacketsReceivedFor(AuthorityFamily.Command));
        TestAssert.AreEqual(1600, meter.BytesSent);
        TestAssert.AreEqual(160.0, meter.SentBytesPerSecond, 0.001, "1600 bytes over 10 seconds.");

        var csv = meter.ToCsv();
        StringAssert.Contains(csv, "label,family,sentPackets,sentBytes,receivedPackets,receivedBytes,seconds,bytesPerSecond");
        StringAssert.Contains(csv, "host,WorldState,2,1500,0,0,10.000,150");
        StringAssert.Contains(csv, "host,Command,0,0,1,40,10.000,0");
        TestAssert.IsFalse(csv.Contains("SnapshotChunk"), "A family with no traffic has no row.");
    }

    [TestMethod]
    public void QueueDepthsAndDeferralsAreRecordedAsHighWaterMarks()
    {
        var meter = new AuthorityPerfMeter("host", windowSize: 8);

        meter.RecordHostCapture(1, 0.5, 1, 10, pendingEvents: 3, deferredBulkBytes: 0, deferredFrames: 0);
        meter.RecordHostCapture(2, 0.5, 1, 10, pendingEvents: 9, deferredBulkBytes: 2048, deferredFrames: 4);
        meter.RecordHostCapture(3, 0.5, 1, 10, pendingEvents: 5, deferredBulkBytes: 512, deferredFrames: 1);
        meter.RecordClientApply(3, 1.5, messages: 12, bytes: 300, queuedAfter: 7, budgetStopped: true);

        TestAssert.AreEqual(9, meter.PeakPendingEvents);
        TestAssert.AreEqual(2048, meter.PeakDeferredBulkBytes);
        TestAssert.AreEqual(4, meter.LongestDeferralFrames);
        TestAssert.AreEqual(7, meter.PeakQueuedApplyMessages);
        TestAssert.AreEqual(1.5, meter.PeakApplyMs);
        TestAssert.AreEqual(1, meter.ApplyBudgetStops);

        meter.RecordDeferral(events: 5, bytes: 700);
        meter.RecordDeferral(events: 2, bytes: 300);
        TestAssert.AreEqual(7, meter.DeferredEvents);
        TestAssert.AreEqual(1000, meter.DeferredBytes);

        var description = string.Join("\n", meter.Describe());
        StringAssert.Contains(description, "backpressure deferredEvents=7 deferredBytes=1000 longestDeferralFrames=4");
    }

    [TestMethod]
    public void WireSizeIsHeaderPlusFramingPlusDeclaredPayload()
    {
        // The fixed header plus the inherited subscription epoch plus the family's own scalars.
        var empty = AuthorityWireSize.ForFamily(AuthorityFamily.WorldState, 0);
        TestAssert.AreEqual(AuthorityLimits.HeaderBytes + 8 + 17, empty);
        TestAssert.AreEqual(empty + 256, AuthorityWireSize.ForFamily(AuthorityFamily.WorldState, 256));

        // A negative or absent declared length cannot shrink the estimate below its fixed framing.
        TestAssert.AreEqual(empty, AuthorityWireSize.ForFamily(AuthorityFamily.WorldState, -5));

        // An unmodelled family still reports the envelope, never a silent zero.
        TestAssert.IsTrue(AuthorityWireSize.ForFamily(AuthorityFamily.None, 100) >= AuthorityLimits.HeaderBytes + 100);
        TestAssert.AreEqual(0, AuthorityWireSize.Of(null));
    }

    [TestMethod]
    public void WireSizeOfAPacketUsesItsDeclaredPayload()
    {
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.WorldState, Epoch,
            connection: default, sequence: 1, hostTick: 5, claimedPlayerId: 1, payloadLength: 0);
        var scope = new ScopeKey(PoolKind.GroundEnemy, 101);
        var payload = new byte[300];
        var packet = AuthorityWorldStatePacket.Create(header, scope, declaredBaselineId: 1, recordCount: 1,
            data: payload);

        TestAssert.AreEqual(AuthorityWireSize.ForFamily(AuthorityFamily.WorldState, 300),
            AuthorityWireSize.Of(packet));

        var lifecycle = AuthorityLifecyclePacket.Create(header, scope, recordCount: 0, data: new byte[9]);
        TestAssert.AreEqual(AuthorityWireSize.ForFamily(AuthorityFamily.Lifecycle, 9),
            AuthorityWireSize.Of(lifecycle));
    }

    [TestMethod]
    public void ThePolicyClassifiesBulkApartFromControlTraffic()
    {
        TestAssert.IsTrue(AuthorityBackpressurePolicy.IsBulk(AuthorityFamily.WorldState));
        TestAssert.IsFalse(AuthorityBackpressurePolicy.IsCritical(AuthorityFamily.WorldState));

        foreach (var family in new[]
                 {
                     AuthorityFamily.Welcome, AuthorityFamily.CommandResult, AuthorityFamily.SnapshotBegin,
                     AuthorityFamily.SnapshotChunk, AuthorityFamily.SnapshotCommit, AuthorityFamily.Lifecycle,
                     AuthorityFamily.ScopeDigest
                 })
        {
            TestAssert.IsTrue(AuthorityBackpressurePolicy.IsCritical(family),
                family + " must never be postponed by the bulk budget.");
        }

        TestAssert.IsTrue(AuthorityBackpressurePolicy.Default.BudgetsBulk);
        TestAssert.IsFalse(AuthorityBackpressurePolicy.Unbounded.BudgetsBulk);
        TestAssert.IsFalse(AuthorityBackpressurePolicy.Unbounded.CoalesceStatePerKey,
            "Unbounded keeps the pre-A23 delivery shape exactly.");

        var families = new List<AuthorityFamily>();
        foreach (AuthorityFamily family in Enum.GetValues(typeof(AuthorityFamily))) families.Add(family);
        TestAssert.IsTrue(families.Count <= AuthorityPerfMeter.FamilyCount,
            "The meter's per-family tables must cover every family value.");
    }
}
