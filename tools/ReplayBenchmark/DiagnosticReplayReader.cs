using System.Diagnostics;
using FortniteReplayReader;
using Microsoft.Extensions.Logging.Abstractions;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ReplayBenchmark;

/// <summary>
/// Diagnostic-only timing reader. It is never used by the acceptance benchmark: stage timings
/// are inclusive, and ReceiveProperties instrumentation intentionally changes its cost profile.
/// </summary>
internal sealed class DiagnosticReplayReader : ReplayReader
{
    private readonly Dictionary<string, StageAccumulator> _stages = new(StringComparer.Ordinal)
    {
        ["Decompress"] = new(),
        ["ReadReplayData"] = new(),
        ["ReadDemoFrameIntoPlaybackPackets"] = new(),
        ["ReadPacket"] = new(),
        ["ReceiveProperties"] = new()
    };

    public DiagnosticReplayReader() : base(NullLogger.Instance, ParseMode.Normal) { }

    public IReadOnlyList<ReplayDiagnosticStage> Stages => _stages
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Value.ToSnapshot(pair.Key))
        .ToArray();

    protected override FArchive Decompress(FArchive archive) =>
        Measure("Decompress", () => base.Decompress(archive));

    public override void ReadReplayData(FArchive archive, int fallbackChunkSize) =>
        Measure("ReadReplayData", () => base.ReadReplayData(archive, fallbackChunkSize));

    public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive) =>
        Measure("ReadDemoFrameIntoPlaybackPackets", () => base.ReadDemoFrameIntoPlaybackPackets(archive));

    public override PacketState ReadPacket(FArchive archive) =>
        Measure("ReadPacket", () => base.ReadPacket(archive));

    public override bool ReceiveProperties(
        FBitArchive archive,
        NetFieldExportGroup group,
        uint channelIndex,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out INetFieldExportGroup? exportGroup,
        bool enablePropertyChecksum = true,
        bool netDeltaUpdate = false)
    {
        var accumulator = _stages["ReceiveProperties"];
        var timestamp = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            return base.ReceiveProperties(archive, group, channelIndex, out exportGroup,
                enablePropertyChecksum, netDeltaUpdate);
        }
        finally
        {
            accumulator.Add(Stopwatch.GetTimestamp() - timestamp,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
    }

    private void Measure(string stageName, Action action)
    {
        var accumulator = _stages[stageName];
        var timestamp = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try { action(); }
        finally
        {
            accumulator.Add(Stopwatch.GetTimestamp() - timestamp,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
    }

    private T Measure<T>(string stageName, Func<T> action)
    {
        var accumulator = _stages[stageName];
        var timestamp = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try { return action(); }
        finally
        {
            accumulator.Add(Stopwatch.GetTimestamp() - timestamp,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
    }

    private sealed class StageAccumulator
    {
        private long _calls;
        private long _ticks;
        private long _allocatedBytes;

        public void Add(long ticks, long allocatedBytes)
        {
            _calls++;
            _ticks += ticks;
            _allocatedBytes += allocatedBytes;
        }

        public ReplayDiagnosticStage ToSnapshot(string name) => new(
            name,
            _calls,
            _ticks * 1000d / Stopwatch.Frequency,
            _allocatedBytes);
    }
}
