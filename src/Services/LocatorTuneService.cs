using ESPresense.Locators;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Wizard block 3b: locator parameter tuning via walk-test replay. Walk test points provide ground
/// truth (known device position) AND realistic input noise (raw per-tick node distances - exactly
/// what the locators consume live). Each nadaraya_watson bandwidth/kernel candidate is replayed
/// over every tick of every point; scored on mean 2D position error (accuracy) plus the standard
/// deviation of the estimates while the beacon sat still (jitter - the room-flapping symptom).
///
/// Honest limitations: stationary noise only (no walking-motion dynamics), and the Kalman/scenario
/// smoothing layered above the locators isn't replayed - this scores the locator geometry itself.
/// </summary>
public class LocatorTuneService(State state, WalkTestService walkTest, ConfigLoader configLoader)
{
    public class Candidate
    {
        public string Key { get; set; } = "";
        public double Bandwidth { get; set; }
        public string Kernel { get; set; } = "gaussian";
        public string Label { get; set; } = "";
    }

    public class CandidateResult
    {
        public Candidate Candidate { get; set; } = new();
        public double MeanErrorM { get; set; }
        public double MeanJitterM { get; set; }
        public double Score { get; set; }
        public int Ticks { get; set; }
        public int Points { get; set; }
        public bool IsCurrent { get; set; }
    }

    public class RunResult
    {
        public string? Error { get; set; }
        public List<CandidateResult> Results { get; set; } = new();
        public string? Recommendation { get; set; }
        public int PointsUsed { get; set; }
        public int TicksUsed { get; set; }
        public DateTime? RanAt { get; set; }
    }

    /// <summary>Jitter weight in the combined score: error counts full, instability half.</summary>
    private const double JitterWeight = 0.5;
    private const int MinTicksPerPoint = 5;

    private RunResult _last = new();

    public RunResult Status() => _last;

    public RunResult Run()
    {
        var result = new RunResult { RanAt = DateTime.UtcNow };
        try
        {
            var points = walkTest.GetPoints().Where(p => p.Raw.Count > 0 && p.FloorId != null).ToList();
            if (points.Count == 0)
            {
                result.Error = "No walk test points with raw tick data - record at least one walk test first (points recorded before this feature have no raw data).";
                _last = result;
                return result;
            }

            var nwConfig = configLoader.Config?.Locators?.NadarayaWatson;
            var currentBandwidth = nwConfig?.Bandwidth ?? 0.5;
            var currentKernel = nwConfig?.Kernel ?? "gaussian";

            var candidates = new List<Candidate>();
            foreach (var bw in new[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
                candidates.Add(new Candidate { Key = $"gaussian:{bw}", Bandwidth = bw, Kernel = "gaussian", Label = $"gaussian, bandwidth {bw}" });
            candidates.Add(new Candidate { Key = "inverse", Bandwidth = 0, Kernel = "inverse_square", Label = "inverse-distance-squared (no bandwidth)" });
            // Ensure the currently configured combination is always in the list.
            if (candidates.All(c => !(c.Kernel == currentKernel && Math.Abs(c.Bandwidth - currentBandwidth) < 0.001)) && currentKernel == "gaussian")
                candidates.Add(new Candidate { Key = $"gaussian:{currentBandwidth}", Bandwidth = currentBandwidth, Kernel = "gaussian", Label = $"gaussian, bandwidth {currentBandwidth} (current)" });

            var results = new List<CandidateResult>();
            var totalTicks = 0;

            foreach (var candidate in candidates)
            {
                var perTickErrors = new List<double>();
                var perPointJitters = new List<double>();
                var pointsUsed = 0;

                foreach (var point in points)
                {
                    var truth = new Point3D(point.X, point.Y, point.Z);
                    var estimates = new List<Point3D>();

                    foreach (var tickGroup in point.Raw.GroupBy(r => r.T))
                    {
                        // Same-floor nodes only - mirrors the live locator's floor filter.
                        var heard = new List<(Point3D loc, double dist)>();
                        foreach (var entry in tickGroup)
                        {
                            if (!state.Nodes.TryGetValue(entry.N, out var node) || !node.HasLocation) continue;
                            if (point.FloorId != null &&
                                !(node.Floors?.Any(f => string.Equals(f.Id, point.FloorId, StringComparison.OrdinalIgnoreCase)) ?? false)) continue;
                            heard.Add((node.Location, entry.D));
                        }
                        if (heard.Count < 3) continue;
                        heard.Sort((a, b) => a.dist.CompareTo(b.dist));

                        var (est, _) = NadarayaWatsonMultilateralizer.Estimate(heard, candidate.Bandwidth, candidate.Kernel);
                        estimates.Add(est);
                        // 2D error - Z is dominated by node mounting heights, not locator quality.
                        perTickErrors.Add(Math.Sqrt(Math.Pow(est.X - truth.X, 2) + Math.Pow(est.Y - truth.Y, 2)));
                    }

                    if (estimates.Count < MinTicksPerPoint) continue;
                    pointsUsed++;

                    var cx = estimates.Average(e => e.X);
                    var cy = estimates.Average(e => e.Y);
                    var jitter = Math.Sqrt(estimates.Average(e => Math.Pow(e.X - cx, 2) + Math.Pow(e.Y - cy, 2)));
                    perPointJitters.Add(jitter);
                }

                if (perTickErrors.Count == 0) continue;
                totalTicks = Math.Max(totalTicks, perTickErrors.Count);

                var meanError = perTickErrors.Average();
                var meanJitter = perPointJitters.Count > 0 ? perPointJitters.Average() : 0;
                results.Add(new CandidateResult
                {
                    Candidate = candidate,
                    MeanErrorM = meanError,
                    MeanJitterM = meanJitter,
                    Score = meanError + JitterWeight * meanJitter,
                    Ticks = perTickErrors.Count,
                    Points = pointsUsed,
                    IsCurrent = candidate.Kernel == currentKernel &&
                                (currentKernel != "gaussian" || Math.Abs(candidate.Bandwidth - currentBandwidth) < 0.001)
                });
            }

            if (results.Count == 0)
            {
                result.Error = "No candidate produced estimates - are the walk points on floors with at least 3 nodes hearing the device?";
                _last = result;
                return result;
            }

            result.Results = results.OrderBy(r => r.Score).ToList();
            result.PointsUsed = points.Count;
            result.TicksUsed = totalTicks;

            var best = result.Results[0];
            var current = result.Results.FirstOrDefault(r => r.IsCurrent);
            if (current != null && best.Score >= current.Score - 0.02)
                result.Recommendation = "Current locator settings already perform best (or within noise of the best) on the recorded walk points.";
            else
                result.Recommendation = $"Best on walk-test replay: {best.Candidate.Label} - mean error {best.MeanErrorM:0.00}m, jitter {best.MeanJitterM:0.00}m" +
                                        (current != null ? $" (current: {current.MeanErrorM:0.00}m / {current.MeanJitterM:0.00}m)" : "") + ".";

            Log.Information("Locator tune: {Count} candidates over {Points} points, best={Best}",
                results.Count, points.Count, best.Candidate.Label);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Locator tune failed");
            result.Error = ex.Message;
        }

        _last = result;
        return result;
    }

    public async Task<(bool ok, string? error)> Apply(string candidateKey)
    {
        var r = _last.Results.FirstOrDefault(x => x.Candidate.Key == candidateKey);
        if (r == null) return (false, $"Unknown candidate '{candidateKey}' - run the locator tune first");

        var c = configLoader.Config;
        if (c == null) return (false, "Config not loaded");

        if (r.Candidate.Kernel == "gaussian")
        {
            c.Locators.NadarayaWatson.Kernel = "gaussian";
            c.Locators.NadarayaWatson.Bandwidth = r.Candidate.Bandwidth;
        }
        else
        {
            // Anything other than "gaussian" makes the locator fall back to inverse-distance-squared.
            c.Locators.NadarayaWatson.Kernel = "inverse_square";
        }
        await configLoader.SaveSectionAsync("locators", c.Locators);
        Log.Information("Locator tune applied: kernel={Kernel}, bandwidth={Bandwidth}", r.Candidate.Kernel, r.Candidate.Bandwidth);
        return (true, null);
    }
}
