using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Optimizers;

/// <summary>
/// The ground-truth half of an optimization cycle. <see cref="OptimizationCycle"/> only reads through this, so a
/// cycle still cannot write anything.
/// </summary>
internal interface IGroundTruthGate
{
    /// <summary>Nodes whose absorption is set by ground truth. Node-pair optimizers must not propose one for them.</summary>
    IReadOnlySet<string> CoveredNodes { get; }

    /// <summary>Whether <see cref="Check"/> can judge at all (the walk-point gate is on).</summary>
    bool CanJudge { get; }

    OptimizerWalkPointGate Check(string optimizerName, OptimizationResults results);
}

/// <summary>
/// Replays a proposal over the recorded walk points and compares it with the current calibration. Distances are
/// recomputed from the recorded LEVELS, so this needs points that recorded levels; older recordings only carry the
/// finished distance, which is blind to a calibration change.
///
/// The node-pair checks only say how well the nodes agree with each other, and a change can improve that while
/// positioning gets worse: that is how quality fell unnoticed from r 0.88 to 0.49. Room hit rate is what presence
/// automations consume, so it is judged first, then the median error. If either gets worse beyond its tolerance the
/// proposal is rejected: better to keep a calibration that demonstrably works.
/// </summary>
internal sealed class WalkPointGate : IGroundTruthGate
{
    private readonly CalibrationBenchmark _benchmark;
    private readonly ConfigOptimization _optimization;
    private readonly BenchmarkResult? _baseline;

    public WalkPointGate(CalibrationBenchmark benchmark, WalkPointAbsorptionOptimizer walkFit, ConfigOptimization optimization)
    {
        _benchmark = benchmark;
        _optimization = optimization;
        CoveredNodes = walkFit.CoveredNodes();
        // The baseline must be computed exactly like a candidate. Without overrides the benchmark uses the RECORDED
        // distances, the calibration of back then frozen into the recording, so every difference could pass as an
        // improvement. The baseline therefore gets today's values as overrides.
        _baseline = optimization.WalkPointGate
            ? benchmark.Run(label: "gate-baseline", overrides: benchmark.CurrentCalibrationOverrides(), remember: false)
            : null;
    }

    public IReadOnlySet<string> CoveredNodes { get; }

    public bool CanJudge => _baseline != null;

    public OptimizerWalkPointGate Check(string optimizerName, OptimizationResults results)
    {
        if (_baseline == null)
            return new OptimizerWalkPointGate(null, null, null, null, true, "walk-point gate is off");
        if (_baseline.Error != null || _baseline.RoomHitRate == null || _baseline.MedianErrorM == null)
            return new OptimizerWalkPointGate(null, null, null, null, true, "no usable walk-point baseline");

        // Candidate = today's values overwritten with what it proposes, so baseline and candidate differ ONLY in the
        // proposed values; nodes it leaves alone would otherwise fall back to the value in the recording.
        var current = _benchmark.CurrentCalibrationOverrides();
        var absorption = new Dictionary<string, double>(current.AbsorptionByNode ?? new(), StringComparer.OrdinalIgnoreCase);
        var rxAdj = new Dictionary<string, double>(current.RxAdjByNode ?? new(), StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        foreach (var (id, r) in results.Nodes)
        {
            if (r.Absorption is { } a) { absorption[id] = a; changed++; }
            if (r.RxAdjRssi is { } x) { rxAdj[id] = x; changed++; }
        }

        if (changed == 0)
            return new OptimizerWalkPointGate(_baseline.RoomHitRate, null, _baseline.MedianErrorM, null, true, "proposes nothing the walk points can judge");

        var candidate = _benchmark.Run(label: $"gate-{optimizerName}", remember: false,
            overrides: new BenchmarkOverrides
            {
                AbsorptionByNode = absorption.Count > 0 ? absorption : null,
                RxAdjByNode = rxAdj.Count > 0 ? rxAdj : null
            });

        if (candidate.Error != null || candidate.RoomHitRate == null || candidate.MedianErrorM == null)
            return new OptimizerWalkPointGate(_baseline.RoomHitRate, null, _baseline.MedianErrorM, null, true, "candidate could not be replayed");

        // Not one distance recomputed means the points carry no levels and the comparison would be with itself.
        if (candidate.RecomputedTicks == 0)
            return new OptimizerWalkPointGate(_baseline.RoomHitRate, candidate.RoomHitRate, _baseline.MedianErrorM, candidate.MedianErrorM, true, "recorded points carry no levels to replay");

        var roomDrop = _baseline.RoomHitRate.Value - candidate.RoomHitRate.Value;
        var medianGrowth = candidate.MedianErrorM.Value - _baseline.MedianErrorM.Value;
        var passed = roomDrop <= _optimization.WalkGateRoomDrop && medianGrowth <= _optimization.WalkGateMedianToleranceM;
        return new OptimizerWalkPointGate(_baseline.RoomHitRate, candidate.RoomHitRate, _baseline.MedianErrorM, candidate.MedianErrorM, passed, null);
    }
}
