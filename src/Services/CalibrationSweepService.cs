using ESPresense.Models;
using ESPresense.Optimizers;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Fits the calibration several different ways and scores each one against the recorded walk points.
///
/// The existing <see cref="LocatorTuneService"/> sweeps the LOCATOR (nadaraya_watson bandwidth and
/// kernel). Nothing swept the CALIBRATION, so every question about absorption, the regularization
/// target or the objective function had to be answered by editing config.yaml, waiting an hour for
/// the optimizer, and forming an impression. That is how the interval-midpoint target survived
/// unnoticed while every node on the installation fitted well above it.
///
/// Each candidate runs the real optimizer on the real snapshot, and the resulting per-node values go
/// through <see cref="CalibrationBenchmark"/> as a set. Scoring a set rather than a single global
/// number is the point: the optimizer fits every node separately, so "what if absorption were 4.0
/// everywhere" is not a question it ever answers.
///
/// ★ The one number that decides whether any of this means anything is <see
/// cref="SweepResult.RespondingPoints"/>. Only walk points recorded with per-tick signal levels can
/// react to a calibration change at all; the rest replay a distance the node derived long ago and
/// stay put no matter what. On this installation that is 5 points out of 26, so the headline median
/// is four-fifths ballast and looks flat even when the responding points move a lot. Reported
/// prominently rather than buried, because reading a flat curve as "makes no difference" is the
/// obvious mistake and I made it myself before the figure existed.
/// </summary>
public class CalibrationSweepService(
    State state,
    ConfigLoader configLoader,
    NodeSettingsStore nodeSettings,
    CalibrationBenchmark benchmark)
{
    public SweepResult Run(SweepRequest? request = null)
    {
        var result = new SweepResult { RanAt = DateTime.UtcNow };
        var optimization = configLoader.Config?.Optimization;
        if (optimization == null)
        {
            result.Error = "No optimization section in the configuration - nothing to sweep.";
            return result;
        }

        var snapshot = state.TakeOptimizationSnapshot();
        if (snapshot.Measures.Count == 0)
        {
            result.Error = "No node-to-node measurements available right now. The nodes need to have " +
                           "heard each other recently for a calibration fit to be possible at all.";
            return result;
        }

        var existing = snapshot.GetNodeIds().ToDictionary(id => id, nodeSettings.Get);
        var candidates = request?.Candidates is { Count: > 0 } supplied
            ? supplied
            : DefaultCandidates(optimization);

        // The state as it actually runs, with no optimizer involved - the line every candidate has
        // to beat to be worth adopting.
        var baseline = benchmark.Run("sweep baseline (as recorded)",
            request?.RefRssi is { } r ? new BenchmarkOverrides { RefRssi = r } : null,
            remember: false);
        result.Baseline = Summarize(baseline, "as recorded", null);

        foreach (var candidate in candidates)
        {
            try
            {
                var optimizer = new PerNodeAbsorptionRxTx(state)
                {
                    ObjectiveOverride = candidate.Objective,
                    AbsorptionPenaltyOverride = candidate.AbsorptionPenalty,
                    AbsorptionMinOverride = candidate.AbsorptionMin,
                    AbsorptionMaxOverride = candidate.AbsorptionMax,
                    AbsorptionTargetOverride = candidate.AbsorptionTarget,
                    HuberDeltaOverride = candidate.HuberDeltaDb
                };

                var fit = optimizer.Optimize(snapshot, existing);
                if (fit.Nodes.Count == 0)
                {
                    result.Runs.Add(new SweepRun { Label = candidate.Label, Error = "The fit produced no values." });
                    continue;
                }

                var absorption = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var rxAdj = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, node) in fit.Nodes)
                {
                    if (node.Absorption is { } a) absorption[id] = a;
                    if (node.RxAdjRssi is { } x) rxAdj[id] = x;
                }

                var scored = benchmark.Run($"sweep: {candidate.Label}", new BenchmarkOverrides
                {
                    RefRssi = request?.RefRssi,
                    AbsorptionByNode = absorption,
                    RxAdjByNode = rxAdj
                }, remember: false);

                var run = Summarize(scored, candidate.Label, candidate);
                run.TargetAbsorption = Math.Round(optimizer.LastTargetAbsorption, 2);
                if (absorption.Count > 0)
                {
                    var sorted = absorption.Values.OrderBy(a => a).ToList();
                    run.AbsorptionMinFitted = Math.Round(sorted[0], 2);
                    run.AbsorptionMedianFitted = Math.Round(sorted[sorted.Count / 2], 2);
                    run.AbsorptionMaxFitted = Math.Round(sorted[^1], 2);
                }
                run.PerPoint = scored.Points.ToDictionary(p => p.Id, p => p.MedianErrorM ?? 0);
                result.Runs.Add(run);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Calibration sweep candidate {Label} failed", candidate.Label);
                result.Runs.Add(new SweepRun { Label = candidate.Label, Error = ex.Message });
            }
        }

        MarkResponding(result);

        var best = result.Runs.Where(r => r.Error == null && r.RespondingMedianErrorM.HasValue)
                              .OrderBy(r => r.RespondingMedianErrorM)
                              .FirstOrDefault();
        result.Verdict = BuildVerdict(result, best);
        result.Runs = result.Runs.OrderBy(r => r.RespondingMedianErrorM ?? double.MaxValue).ToList();
        return result;
    }

    /// <summary>
    /// Works out which walk points reacted at all, then re-scores every candidate over just those.
    /// A point that cannot respond contributes the same error to every candidate, so including it
    /// only dilutes the comparison - with 21 of 26 unable to respond here, it dilutes it to nothing.
    /// </summary>
    private static void MarkResponding(SweepResult result)
    {
        var runs = result.Runs.Where(r => r.Error == null).ToList();
        if (runs.Count < 2) return;

        var ids = runs.SelectMany(r => r.PerPoint.Keys).Distinct().ToList();
        var responding = ids.Where(id =>
        {
            var values = runs.Where(r => r.PerPoint.ContainsKey(id)).Select(r => Math.Round(r.PerPoint[id], 3)).Distinct().ToList();
            return values.Count > 1;
        }).ToHashSet();

        result.RespondingPoints = responding.Count;
        result.TotalPoints = ids.Count;
        result.RespondingPointIds = responding.OrderBy(i => i, StringComparer.Ordinal).ToList();

        foreach (var run in runs)
        {
            var values = responding.Where(run.PerPoint.ContainsKey).Select(id => run.PerPoint[id]).OrderBy(v => v).ToList();
            if (values.Count == 0) continue;
            run.RespondingMedianErrorM = Math.Round(
                values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2.0, 2);
        }
    }

    private static string BuildVerdict(SweepResult result, SweepRun? best)
    {
        if (best == null) return "No candidate produced a usable fit.";

        if (result.RespondingPoints == 0)
            return "None of the walk points carry per-tick signal levels, so no calibration change " +
                   "can move them. The comparison below is meaningless until points are recorded " +
                   "with a firmware that stores levels - record a fresh walk point and it will.";

        var share = result.TotalPoints > 0 ? (double)result.RespondingPoints / result.TotalPoints : 0;
        var caveat = share < 0.5
            ? $" Only {result.RespondingPoints} of {result.TotalPoints} walk points can respond to a " +
              "calibration change at all, so treat this as a hint rather than a verdict - the " +
              "remaining points replay a distance that was derived before the recording and cannot move."
            : "";

        return $"Best: {best.Label} at {best.RespondingMedianErrorM:0.00} m over the responding points" + caveat;
    }

    /// <summary>
    /// The grid that answers the questions actually open on this fork: does a dB residual with
    /// robust weighting beat squared metres with a one-sided 4th power, does the regularization earn
    /// its keep, and do the limits still bind once the target no longer sits between them.
    /// </summary>
    private static List<SweepCandidate> DefaultCandidates(ConfigOptimization o) =>
    [
        new() { Label = "as configured", Objective = "distance", AbsorptionPenalty = o.AbsorptionPenaltyWeight },
        new() { Label = "distance, no regularization", Objective = "distance", AbsorptionPenalty = 0 },
        new() { Label = "dB + Huber", Objective = "db", AbsorptionPenalty = o.AbsorptionPenaltyWeight },
        new() { Label = "dB + Huber, no regularization", Objective = "db", AbsorptionPenalty = 0 },
        new() { Label = "dB + Huber, wider limits", Objective = "db", AbsorptionPenalty = o.AbsorptionPenaltyWeight, AbsorptionMin = 1.5, AbsorptionMax = 6.5 },
        new() { Label = "dB + Huber, wider limits, no regularization", Objective = "db", AbsorptionPenalty = 0, AbsorptionMin = 1.5, AbsorptionMax = 6.5 }
    ];

    private static SweepRun Summarize(BenchmarkResult r, string label, SweepCandidate? candidate) => new()
    {
        Label = label,
        Objective = candidate?.Objective,
        AbsorptionPenalty = candidate?.AbsorptionPenalty,
        AbsorptionMin = candidate?.AbsorptionMin,
        AbsorptionMax = candidate?.AbsorptionMax,
        Error = r.Error,
        MedianErrorM = r.MedianErrorM,
        P90ErrorM = r.P90ErrorM,
        RoomHitRate = r.RoomHitRate,
        FloorHitRate = r.FloorHitRate,
        Ticks = r.Ticks,
        PerPoint = r.Points.ToDictionary(p => p.Id, p => p.MedianErrorM ?? 0)
    };
}

public class SweepRequest
{
    /// <summary>Device reference level to hold constant across the sweep, so only the fit varies.</summary>
    public double? RefRssi { get; set; }
    public List<SweepCandidate>? Candidates { get; set; }
}

public class SweepCandidate
{
    public string Label { get; set; } = "";
    public string? Objective { get; set; }
    public double? AbsorptionPenalty { get; set; }
    public double? AbsorptionTarget { get; set; }
    public double? AbsorptionMin { get; set; }
    public double? AbsorptionMax { get; set; }
    public double? HuberDeltaDb { get; set; }
}

public class SweepResult
{
    public DateTime RanAt { get; set; }
    public string? Error { get; set; }
    public string? Verdict { get; set; }

    /// <summary>How many walk points can respond to a calibration change at all.</summary>
    public int RespondingPoints { get; set; }
    public int TotalPoints { get; set; }
    public List<string> RespondingPointIds { get; set; } = new();

    public SweepRun? Baseline { get; set; }
    public List<SweepRun> Runs { get; set; } = new();
}

public class SweepRun
{
    public string Label { get; set; } = "";
    public string? Objective { get; set; }
    public double? AbsorptionPenalty { get; set; }
    public double? AbsorptionMin { get; set; }
    public double? AbsorptionMax { get; set; }
    public string? Error { get; set; }

    public double? MedianErrorM { get; set; }
    /// <summary>Median over only the points that can respond - the figure worth comparing.</summary>
    public double? RespondingMedianErrorM { get; set; }
    public double? P90ErrorM { get; set; }
    public double? RoomHitRate { get; set; }
    public double? FloorHitRate { get; set; }
    public int Ticks { get; set; }

    /// <summary>What the regularization actually pulled towards for this candidate.</summary>
    public double? TargetAbsorption { get; set; }
    public double? AbsorptionMinFitted { get; set; }
    public double? AbsorptionMedianFitted { get; set; }
    public double? AbsorptionMaxFitted { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, double> PerPoint { get; set; } = new();
}
