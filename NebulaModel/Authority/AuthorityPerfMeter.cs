#region

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Latency distribution of one periodic cost, in milliseconds.
/// </summary>
/// <remarks>
/// The percentile is computed from a bounded sample window rather than an unbounded history, so a
/// long run cannot grow the meter's memory: the meter's own footprint is fixed, which is what makes
/// "no continuous growth" a property a test can check instead of a hope.
/// </remarks>
public readonly struct AuthorityLatencySummary
{
    public AuthorityLatencySummary(int count, double p50, double p95, double max, double mean)
    {
        Count = count;
        P50Ms = p50;
        P95Ms = p95;
        MaxMs = max;
        MeanMs = mean;
    }

    /// <summary>Samples in the retained window.</summary>
    public int Count { get; }

    public double P50Ms { get; }
    public double P95Ms { get; }
    public double MaxMs { get; }
    public double MeanMs { get; }

    public static AuthorityLatencySummary Empty => new(0, 0, 0, 0, 0);

    public override string ToString() => "n=" + Count + " p50=" + P50Ms.ToString("F3", CultureInfo.InvariantCulture) +
                                        "ms p95=" + P95Ms.ToString("F3", CultureInfo.InvariantCulture) +
                                        "ms max=" + MaxMs.ToString("F3", CultureInfo.InvariantCulture) + "ms";
}

/// <summary>
/// The authority session's performance meter (TASKS.md A23, VALIDATION §9).
/// </summary>
/// <remarks>
/// <para>
/// The meter answers the four questions the validation budget asks and nothing else: how long the
/// host's capture takes per tick, how long a client's apply takes per frame, how many bytes travel
/// per family, and how deep the queues get. Every counter here is either monotonic or a bounded
/// window, so a two-hour run and a two-second run have the same memory footprint.
/// </para>
/// <para>
/// Time is expressed in host ticks, not wall clock, wherever a rate is computed. VALIDATION §8 is
/// explicit that a local wall clock cannot compare two instances, and the same reasoning applies to
/// a throughput number: bytes per host tick is comparable across measurements, bytes per wall second
/// is a property of the machine that produced it.
/// </para>
/// </remarks>
public sealed class AuthorityPerfMeter
{
    /// <summary>Families the per-family counters cover; the enum's highest value plus one.</summary>
    public const int FamilyCount = 12;

    private readonly double[] captureWindow;
    private readonly double[] applyWindow;
    private readonly int windowSize;
    private readonly long[] packetsSentByFamily = new long[FamilyCount];
    private readonly long[] bytesSentByFamily = new long[FamilyCount];
    private readonly long[] packetsReceivedByFamily = new long[FamilyCount];
    private readonly long[] bytesReceivedByFamily = new long[FamilyCount];

    private int captureCount;
    private int captureNext;
    private double captureTotalMs;
    private int applyCount;
    private int applyNext;
    private double applyTotalMs;

    private long firstHostTick;
    private long lastHostTick;
    private bool hasHostTick;

    /// <param name="label">Role label used in <see cref="Describe"/> ("host"/"client1").</param>
    /// <param name="ticksPerSecond">Host tick rate the byte rate is expressed against.</param>
    /// <param name="windowSize">Retained latency samples per cost.</param>
    public AuthorityPerfMeter(string label = "authority", double ticksPerSecond = 60.0, int windowSize = 512)
    {
        if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        Label = label ?? "authority";
        TicksPerSecond = ticksPerSecond;
        this.windowSize = windowSize;
        captureWindow = new double[windowSize];
        applyWindow = new double[windowSize];
    }

    public string Label { get; }

    public double TicksPerSecond { get; }

    public int WindowSize => windowSize;

    // ---- host capture ---------------------------------------------------------------------

    public long CaptureFrames { get; private set; }
    public long CapturePackets { get; private set; }
    public long CaptureBytes { get; private set; }

    /// <summary>Worst capture cost observed, kept as a monotonic high-water mark.</summary>
    public double PeakCaptureMs { get; private set; }

    /// <summary>Deepest retained-event backlog seen after a capture, across all scopes.</summary>
    public int PeakPendingEvents { get; private set; }

    /// <summary>Largest amount of bulk traffic one frame postponed for one subscriber.</summary>
    public long PeakDeferredBulkBytes { get; private set; }

    /// <summary>State events the bulk budget postponed, cumulative.</summary>
    public long DeferredEvents { get; private set; }

    /// <summary>Bytes the bulk budget postponed, cumulative.</summary>
    public long DeferredBytes { get; private set; }

    /// <summary>
    /// Longest number of consecutive frames a subscriber had something postponed (VALIDATION §9's
    /// "记录最长等待"). One frame is normal backpressure; a growing number is starvation.
    /// </summary>
    public int LongestDeferralFrames { get; private set; }

    /// <summary>Records one host capture pass.</summary>
    public void RecordHostCapture(long hostTick, double milliseconds, int packets, long bytes,
        int pendingEvents, long deferredBulkBytes, int deferredFrames)
    {
        CaptureFrames++;
        CapturePackets += packets;
        CaptureBytes += bytes;
        if (milliseconds > PeakCaptureMs) PeakCaptureMs = milliseconds;
        if (pendingEvents > PeakPendingEvents) PeakPendingEvents = pendingEvents;
        if (deferredBulkBytes > PeakDeferredBulkBytes) PeakDeferredBulkBytes = deferredBulkBytes;
        if (deferredFrames > LongestDeferralFrames) LongestDeferralFrames = deferredFrames;
        Push(captureWindow, ref captureCount, ref captureNext, ref captureTotalMs, milliseconds);
        TouchTick(hostTick);
    }

    /// <summary>Records a postponement, for the accumulated counters.</summary>
    public void RecordDeferral(int events, long bytes)
    {
        DeferredEvents += events;
        DeferredBytes += bytes;
    }

    // ---- client apply ---------------------------------------------------------------------

    public long ApplyFrames { get; private set; }
    public long ApplyMessages { get; private set; }
    public long ApplyBytes { get; private set; }

    public double PeakApplyMs { get; private set; }

    /// <summary>Deepest apply queue left behind after a frame, in messages.</summary>
    public int PeakQueuedApplyMessages { get; private set; }

    /// <summary>Frames that stopped early because the apply budget ran out.</summary>
    public long ApplyBudgetStops { get; private set; }

    /// <summary>Records one client apply pass.</summary>
    public void RecordClientApply(long hostTick, double milliseconds, int messages, long bytes,
        int queuedAfter, bool budgetStopped)
    {
        ApplyFrames++;
        ApplyMessages += messages;
        ApplyBytes += bytes;
        if (milliseconds > PeakApplyMs) PeakApplyMs = milliseconds;
        if (queuedAfter > PeakQueuedApplyMessages) PeakQueuedApplyMessages = queuedAfter;
        if (budgetStopped) ApplyBudgetStops++;
        Push(applyWindow, ref applyCount, ref applyNext, ref applyTotalMs, milliseconds);
        TouchTick(hostTick);
    }

    // ---- traffic --------------------------------------------------------------------------

    public long PacketsSent { get; private set; }
    public long BytesSent { get; private set; }
    public long PacketsReceived { get; private set; }
    public long BytesReceived { get; private set; }

    public void RecordPacketSent(AuthorityFamily family, long bytes)
    {
        var index = (int)family;
        if ((uint)index >= FamilyCount) return;
        packetsSentByFamily[index]++;
        bytesSentByFamily[index] += bytes;
        PacketsSent++;
        BytesSent += bytes;
    }

    public void RecordPacketReceived(AuthorityFamily family, long bytes)
    {
        var index = (int)family;
        if ((uint)index >= FamilyCount) return;
        packetsReceivedByFamily[index]++;
        bytesReceivedByFamily[index] += bytes;
        PacketsReceived++;
        BytesReceived += bytes;
    }

    public long PacketsSentFor(AuthorityFamily family) => Bucket(packetsSentByFamily, family);

    public long BytesSentFor(AuthorityFamily family) => Bucket(bytesSentByFamily, family);

    public long PacketsReceivedFor(AuthorityFamily family) => Bucket(packetsReceivedByFamily, family);

    public long BytesReceivedFor(AuthorityFamily family) => Bucket(bytesReceivedByFamily, family);

    /// <summary>Host ticks spanned by every recorded sample, or zero before two samples exist.</summary>
    public long ElapsedTicks => lastHostTick > firstHostTick ? lastHostTick - firstHostTick : 0;

    /// <summary>Sent bytes per second, derived from host ticks rather than wall clock.</summary>
    public double SentBytesPerSecond => Rate(BytesSent);

    public double ReceivedBytesPerSecond => Rate(BytesReceived);

    // ---- summaries ------------------------------------------------------------------------

    public AuthorityLatencySummary CaptureLatency => Summarize(captureWindow, captureCount, captureTotalMs);

    public AuthorityLatencySummary ApplyLatency => Summarize(applyWindow, applyCount, applyTotalMs);

    /// <summary>One line per fact, for the harness's perf report.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>
        {
            "perf label=" + Label + " frames=" + CaptureFrames + " applyFrames=" + ApplyFrames +
            " elapsedTicks=" + ElapsedTicks,
            "capture " + CaptureLatency + " peak=" + Ms(PeakCaptureMs) + " packets=" + CapturePackets +
            " bytes=" + CaptureBytes,
            "apply " + ApplyLatency + " peak=" + Ms(PeakApplyMs) + " messages=" + ApplyMessages +
            " bytes=" + ApplyBytes + " budgetStops=" + ApplyBudgetStops,
            "queues peakPendingEvents=" + PeakPendingEvents + " peakDeferredBulkBytes=" + PeakDeferredBulkBytes +
            " peakQueuedApplyMessages=" + PeakQueuedApplyMessages,
            "backpressure deferredEvents=" + DeferredEvents + " deferredBytes=" + DeferredBytes +
            " longestDeferralFrames=" + LongestDeferralFrames,
            "traffic sentPackets=" + PacketsSent + " sentBytes=" + BytesSent +
            " sentBytesPerSecond=" + Rate(BytesSent).ToString("F0", CultureInfo.InvariantCulture) +
            " receivedPackets=" + PacketsReceived + " receivedBytes=" + BytesReceived +
            " receivedBytesPerSecond=" + Rate(BytesReceived).ToString("F0", CultureInfo.InvariantCulture)
        };

        foreach (AuthorityFamily family in Enum.GetValues(typeof(AuthorityFamily)))
        {
            var sent = BytesSentFor(family);
            var received = BytesReceivedFor(family);
            if (sent == 0 && received == 0) continue;
            lines.Add("family " + family + " sentPackets=" + PacketsSentFor(family) + " sentBytes=" + sent +
                      " receivedPackets=" + PacketsReceivedFor(family) + " receivedBytes=" + received);
        }
        return lines;
    }

    /// <summary>
    /// Deterministic per-family rate table, for the machine-readable report.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Describe"/> so a script can read counters without parsing prose,
    /// and so a test can assert on exact numbers instead of formatted text.
    /// </remarks>
    public string ToCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("label,family,sentPackets,sentBytes,receivedPackets,receivedBytes,seconds,bytesPerSecond");
        var seconds = TicksPerSecond > 0 ? ElapsedTicks / TicksPerSecond : 0;
        foreach (AuthorityFamily family in Enum.GetValues(typeof(AuthorityFamily)))
        {
            var sent = BytesSentFor(family);
            var received = BytesReceivedFor(family);
            if (sent == 0 && received == 0) continue;
            var perSecond = seconds > 0 ? sent / seconds : 0;
            builder.Append(Label).Append(',').Append(family).Append(',')
                .Append(PacketsSentFor(family)).Append(',').Append(sent).Append(',')
                .Append(PacketsReceivedFor(family)).Append(',').Append(received).Append(',')
                .Append(seconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(perSecond.ToString("F0", CultureInfo.InvariantCulture)).AppendLine();
        }
        return builder.ToString();
    }

    private double Rate(long bytes)
    {
        var seconds = TicksPerSecond > 0 ? ElapsedTicks / TicksPerSecond : 0;
        return seconds > 0 ? bytes / seconds : 0;
    }

    private void TouchTick(long hostTick)
    {
        // Tracked with a flag rather than by comparing against zero: a host tick of 0 is a real
        // moment (the first frame after a load), and treating it as "unset" would silently report an
        // elapsed time that starts one sample late.
        if (!hasHostTick)
        {
            firstHostTick = hostTick;
            hasHostTick = true;
        }
        lastHostTick = hostTick;
    }

    private static long Bucket(long[] table, AuthorityFamily family)
    {
        var index = (int)family;
        return (uint)index < FamilyCount ? table[index] : 0;
    }

    private static string Ms(double value) => value.ToString("F3", CultureInfo.InvariantCulture) + "ms";

    private static void Push(double[] window, ref int count, ref int next, ref double total, double value)
    {
        if (count < window.Length)
        {
            window[next] = value;
            count++;
        }
        else
        {
            // Overwrite the oldest sample: the window is a ring, so the summary always reflects the
            // most recent window rather than a session-long average that hides a regression.
            total -= window[next];
            window[next] = value;
        }
        total += value;
        next = (next + 1) % window.Length;
    }

    private static AuthorityLatencySummary Summarize(double[] window, int count, double total)
    {
        if (count == 0) return AuthorityLatencySummary.Empty;
        var sorted = new double[count];
        Array.Copy(window, sorted, count);
        Array.Sort(sorted);
        double max = sorted[count - 1];
        return new AuthorityLatencySummary(count, Percentile(sorted, 0.50), Percentile(sorted, 0.95), max,
            total / count);
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Length) index = sorted.Length - 1;
        return sorted[index];
    }
}
