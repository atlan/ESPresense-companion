using ESPresense.Models;
using ESPresense.Optimizers;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Wizard block 3: automatic optimizer/hyperparameter selection. Fits each candidate configuration
/// (optimizer type x absorption_penalty) on the currently collected measures and scores it on
/// HELD-OUT node pairs the fit never saw - guarding against the historical failure mode where an
/// over-parameterized fit looks perfect on its own training data (the MLE 3-node bug). Split is by
/// whole (unordered) node pair, not by individual measure: measures of the same pair are near
/// duplicates, splitting them across train/holdout would leak. 3-fold cross-validation,
/// deterministic folds.
///
/// Only evaluates the CALIBRATION FIT parameters. Locator-side settings (e.g. nadaraya_watson
/// bandwidth) affect live positioning, not the fit, and cannot be scored offline against snapshots.
/// </summary>
public class AutoTuneService(State state, NodeSettingsStore nsd, WalkTestService walkTest, ConfigLoader configLoader)
{
    public class Candidate
    {
        public string Key { get; set; } = "";
        public string Optimizer { get; set; } = "";
        public double? AbsorptionPenalty { get; set; }
        public double? AbsorptionMin { get; set; }
        public double? AbsorptionMax { get; set; }
        public string Label { get; set; } = "";
    }

    public class CandidateResult
    {
        public Candidate Candidate { get; set; } = new();
        public double MeanHoldoutComposite { get; set; }
        public double MeanTrainComposite { get; set; }
        public double MeanHoldoutR { get; set; }
        public double MeanHoldoutRmse { get; set; }
        public int Folds { get; set; }
        public bool IsCurrent { get; set; }
    }

    public class RunState
    {
        public bool Running { get; set; }
        public string? Phase { get; set; }
        public int CandidatesDone { get; set; }
        public int CandidatesTotal { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string? Error { get; set; }
        public List<CandidateResult> Results { get; set; } = new();
        public CandidateResult? Baseline { get; set; }
        public string? Recommendation { get; set; }
        public int MeasureCount { get; set; }
        public int PairCount { get; set; }
    }

    private const int Folds = 3;
    private const int MinPairs = 8;

    private readonly object _lock = new();
    private RunState _state = new();

    public RunState Status()
    {
        lock (_lock)
        {
            // Shallow copy is fine - results are replaced wholesale, not mutated in place.
            return new RunState
            {
                Running = _state.Running,
                Phase = _state.Phase,
                CandidatesDone = _state.CandidatesDone,
                CandidatesTotal = _state.CandidatesTotal,
                StartedAt = _state.StartedAt,
                FinishedAt = _state.FinishedAt,
                Error = _state.Error,
                Results = _state.Results,
                Baseline = _state.Baseline,
                Recommendation = _state.Recommendation,
                MeasureCount = _state.MeasureCount,
                PairCount = _state.PairCount
            };
        }
    }

    public (bool ok, string? error) Start()
    {
        lock (_lock)
        {
            if (_state.Running) return (false, "Auto-tune is already running");
            _state = new RunState { Running = true, Phase = "Collecting data", StartedAt = DateTime.UtcNow };
        }
        _ = Task.Run(RunAsync);
        return (true, null);
    }

    private List<Candidate> BuildCandidates()
    {
        var candidates = new List<Candidate>();
        foreach (var penalty in new[] { 0.5, 1.0, 3.0, 10.0 })
        {
            candidates.Add(new Candidate
            {
                Key = $"per_node_absorption:{penalty}",
                Optimizer = "per_node_absorption",
                AbsorptionPenalty = penalty,
                Label = $"per_node_absorption, penalty {penalty}"
            });
        }
        // Absorption BOUNDS variants (limits.absorption_min/max) - a too-narrow window forces
        // nodes with genuinely unusual RF surroundings against the rails; a too-wide one gives
        // the fit rope to overfit. Tested at two penalty levels each.
        foreach (var penalty in new[] { 1.0, 10.0 })
        {
            candidates.Add(new Candidate
            {
                Key = $"per_node_absorption:{penalty}:wide",
                Optimizer = "per_node_absorption",
                AbsorptionPenalty = penalty,
                AbsorptionMin = 1.8,
                AbsorptionMax = 5.5,
                Label = $"per_node_absorption, penalty {penalty}, bounds 1.8-5.5 (wide)"
            });
            candidates.Add(new Candidate
            {
                Key = $"per_node_absorption:{penalty}:narrow",
                Optimizer = "per_node_absorption",
                AbsorptionPenalty = penalty,
                AbsorptionMin = 2.0,
                AbsorptionMax = 4.0,
                Label = $"per_node_absorption, penalty {penalty}, bounds 2.0-4.0 (narrow)"
            });
        }
        candidates.Add(new Candidate
        {
            Key = "global_absorption",
            Optimizer = "global_absorption",
            Label = "global_absorption"
        });
        return candidates;
    }

    private IOptimizer BuildOptimizer(Candidate c)
    {
        return c.Optimizer switch
        {
            "global_absorption" => new GlobalAbsorptionRxTxOptimizer(state),
            _ => new PerNodeAbsorptionRxTx(state)
            {
                AbsorptionPenaltyOverride = c.AbsorptionPenalty,
                AbsorptionMinOverride = c.AbsorptionMin,
                AbsorptionMaxOverride = c.AbsorptionMax
            }
        };
    }

    private static string PairKey(Measure m)
    {
        var a = m.Tx.Id.ToLowerInvariant();
        var b = m.Rx.Id.ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private async Task RunAsync()
    {
        try
        {
            var optimization = state.Config?.Optimization;
            var excludedPairs = optimization?.ExcludedPairs;
            var correlationWeight = optimization?.CorrelationWeight ?? 0.5;
            var rmseWeight = optimization?.RmseWeight ?? 0.5;

            var snapshots = state.GetOptimizationSnapshots();
            var walkMeasures = walkTest.GetExtraMeasures();
            var allMeasures = snapshots.SelectMany(s => s.Measures)
                .Concat(walkMeasures)
                .Where(m => m.Tx?.Id != null && m.Rx?.Id != null)
                .ToList();

            var pairGroups = allMeasures.GroupBy(PairKey).ToList();
            lock (_lock)
            {
                _state.MeasureCount = allMeasures.Count;
                _state.PairCount = pairGroups.Count;
            }

            if (pairGroups.Count < MinPairs)
            {
                Finish(error: $"Not enough data: only {pairGroups.Count} node pairs collected (need >= {MinPairs}). " +
                              "Let snapshot sampling run for a few minutes and/or record walk test points first.");
                return;
            }

            // Deterministic fold assignment per pair (stable string hash - GetHashCode is
            // per-process randomized and would make runs non-reproducible).
            int StableHash(string s)
            {
                unchecked
                {
                    var h = 23;
                    foreach (var ch in s) h = h * 31 + ch;
                    return Math.Abs(h);
                }
            }

            var foldOf = pairGroups.ToDictionary(g => g.Key, g => StableHash(g.Key) % Folds);

            var candidates = BuildCandidates();
            var currentOptimizer = optimization?.Optimizer?.ToLowerInvariant() ?? "legacy";
            var currentPenalty = optimization?.AbsorptionPenaltyWeight ?? 10;

            lock (_lock) _state.CandidatesTotal = candidates.Count;

            double Composite(double corr, double rmse) =>
                (corr * correlationWeight) + ((1 - rmse / (1 + rmse)) * rmseWeight);

            // Baseline: current stored settings, no refit, evaluated per fold on holdout only.
            {
                var holdR = new List<double>();
                var holdRmse = new List<double>();
                for (var fold = 0; fold < Folds; fold++)
                {
                    var holdout = pairGroups.Where(g => foldOf[g.Key] == fold).SelectMany(g => g).ToList();
                    if (holdout.Count == 0) continue;
                    var (corr, rmse) = new OptimizationResults().Evaluate(
                        new List<OptimizationSnapshot> { new() { Measures = holdout } }, nsd, excludedPairs);
                    if (double.IsNaN(corr) || double.IsNaN(rmse)) continue;
                    holdR.Add(corr);
                    holdRmse.Add(rmse);
                }
                if (holdR.Count > 0)
                {
                    var baseline = new CandidateResult
                    {
                        Candidate = new Candidate { Key = "baseline", Label = $"current settings ({currentOptimizer}, penalty {currentPenalty})" },
                        MeanHoldoutR = holdR.Average(),
                        MeanHoldoutRmse = holdRmse.Average(),
                        MeanHoldoutComposite = Composite(holdR.Average(), holdRmse.Average()),
                        Folds = holdR.Count,
                        IsCurrent = true
                    };
                    lock (_lock) _state.Baseline = baseline;
                }
            }

            var results = new List<CandidateResult>();
            foreach (var candidate in candidates)
            {
                lock (_lock) _state.Phase = $"Fitting {candidate.Label}";
                var holdComposites = new List<double>();
                var trainComposites = new List<double>();
                var holdRs = new List<double>();
                var holdRmses = new List<double>();

                for (var fold = 0; fold < Folds; fold++)
                {
                    var train = pairGroups.Where(g => foldOf[g.Key] != fold).SelectMany(g => g).ToList();
                    var holdout = pairGroups.Where(g => foldOf[g.Key] == fold).SelectMany(g => g).ToList();
                    if (train.Count == 0 || holdout.Count == 0) continue;

                    var trainSnapshot = new OptimizationSnapshot { Measures = train };
                    var currentSettings = trainSnapshot.GetNodeIds().ToDictionary(id => id, nsd.Get);

                    // BFGS fits are CPU-bound and can take seconds each - yield the task scheduler.
                    var optimizer = BuildOptimizer(candidate);
                    var fitted = await Task.Run(() => optimizer.Optimize(trainSnapshot, currentSettings));

                    var (trainCorr, trainRmse) = fitted.Evaluate(
                        new List<OptimizationSnapshot> { trainSnapshot }, nsd, excludedPairs);
                    var (holdCorr, holdRmse) = fitted.Evaluate(
                        new List<OptimizationSnapshot> { new() { Measures = holdout } }, nsd, excludedPairs);

                    if (double.IsNaN(holdCorr) || double.IsNaN(holdRmse)) continue;
                    holdComposites.Add(Composite(holdCorr, holdRmse));
                    holdRs.Add(holdCorr);
                    holdRmses.Add(holdRmse);
                    if (!double.IsNaN(trainCorr) && !double.IsNaN(trainRmse))
                        trainComposites.Add(Composite(trainCorr, trainRmse));
                }

                if (holdComposites.Count > 0)
                {
                    results.Add(new CandidateResult
                    {
                        Candidate = candidate,
                        MeanHoldoutComposite = holdComposites.Average(),
                        MeanTrainComposite = trainComposites.Count > 0 ? trainComposites.Average() : double.NaN,
                        MeanHoldoutR = holdRs.Average(),
                        MeanHoldoutRmse = holdRmses.Average(),
                        Folds = holdComposites.Count,
                        IsCurrent = candidate.Optimizer == currentOptimizer &&
                                    (candidate.AbsorptionPenalty == null || Math.Abs(candidate.AbsorptionPenalty.Value - currentPenalty) < 0.001)
                    });
                }

                lock (_lock)
                {
                    _state.CandidatesDone++;
                    _state.Results = results.OrderByDescending(r => r.MeanHoldoutComposite).ToList();
                }
            }

            string? recommendation = null;
            var best = results.OrderByDescending(r => r.MeanHoldoutComposite).FirstOrDefault();
            RunState snapshot = Status();
            if (best != null)
            {
                var baselineScore = snapshot.Baseline?.MeanHoldoutComposite ?? double.NaN;
                if (!double.IsNaN(baselineScore) && best.MeanHoldoutComposite <= baselineScore + 0.005)
                    recommendation = "Current settings already perform as well as the best candidate on held-out data - no change recommended.";
                else if (best.IsCurrent)
                    recommendation = "The currently configured optimizer/penalty is already the best candidate - no change needed.";
                else
                    recommendation = $"Best on held-out data: {best.Candidate.Label} (composite {best.MeanHoldoutComposite:0.000}).";
            }

            Finish(recommendation: recommendation);
            Log.Information("Auto-tune finished: {Count} candidates, best={Best}", results.Count, best?.Candidate.Label);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Auto-tune run failed");
            Finish(error: ex.Message);
        }
    }

    private void Finish(string? error = null, string? recommendation = null)
    {
        lock (_lock)
        {
            _state.Running = false;
            _state.Phase = null;
            _state.FinishedAt = DateTime.UtcNow;
            _state.Error = error;
            _state.Recommendation = recommendation;
        }
    }

    public async Task<(bool ok, string? error)> Apply(string candidateKey)
    {
        RunState snapshot = Status();
        var result = snapshot.Results.FirstOrDefault(r => r.Candidate.Key == candidateKey);
        if (result == null) return (false, $"Unknown candidate '{candidateKey}' - run auto-tune first");

        var c = configLoader.Config;
        if (c == null) return (false, "Config not loaded");

        c.Optimization.Optimizer = result.Candidate.Optimizer;
        if (result.Candidate.AbsorptionPenalty.HasValue)
            c.Optimization.Weights["absorption_penalty"] = result.Candidate.AbsorptionPenalty.Value;
        if (result.Candidate.AbsorptionMin.HasValue)
            c.Optimization.Limits["absorption_min"] = result.Candidate.AbsorptionMin.Value;
        if (result.Candidate.AbsorptionMax.HasValue)
            c.Optimization.Limits["absorption_max"] = result.Candidate.AbsorptionMax.Value;
        await configLoader.SaveSectionAsync("optimization", c.Optimization);
        Log.Information("Auto-tune applied: optimizer={Optimizer}, penalty={Penalty}",
            result.Candidate.Optimizer, result.Candidate.AbsorptionPenalty);
        return (true, null);
    }
}
