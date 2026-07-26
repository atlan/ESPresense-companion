using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// The yardstick. Replays the recorded walk points through the locator with the CURRENT
/// configuration and reports how far off it lands - median, 90th percentile, room hit rate, broken
/// down per floor - and keeps the history so two runs can be compared.
///
/// Why this has to exist before anything else is tuned: an accuracy investigation on 2026-07-26
/// produced three plausible explanations for the same symptom in a row, two of which turned out to
/// be wrong, and each was argued from a different hand-picked slice of the data. Without one number
/// that is computed the same way every time, "this change made it better" is an opinion.
///
/// Distinct from <see cref="LocatorTuneService"/>, which sweeps nadaraya_watson candidates to pick a
/// winner: this measures the configuration as it actually stands, so it is meaningful for changes
/// anywhere in the chain - objective function, parameter bounds, obstacle terms - not just the
/// locator kernel. The replay itself follows the same rules as the tuner (same-floor nodes only, at
/// least three of them, 2D error) so the two stay comparable.
/// </summary>
public class CalibrationBenchmark(
    State state,
    WalkTestService walkTest,
    ConfigLoader configLoader,
    string? persistPath = null)
{
    /// <summary>Fewer usable ticks than this and a point says more about luck than accuracy.</summary>
    private const int MinTicksPerPoint = 5;

    /// <summary>Locators need three ranges for a fix; mirrors the live path.</summary>
    private const int MinNodesPerTick = 3;

    private const int MaxHistory = 50;

    private List<BenchmarkResult> _history = Load(persistPath);

    public BenchmarkResult? Last => _history.LastOrDefault();

    public IReadOnlyList<BenchmarkResult> History => _history;

    /// <param name="remember">
    /// False for exploratory runs. The sweep scores half a dozen candidates in one go; keeping them
    /// would push the real measurements out of a 50-entry history and make "compared with the
    /// previous run" mean "compared with a candidate somebody was trying out".
    /// </param>
    public BenchmarkResult Run(string? label = null, BenchmarkOverrides? overrides = null, bool remember = true)
    {
        var result = new BenchmarkResult { RanAt = DateTime.UtcNow, Label = label, Overrides = overrides };

        var allPoints = walkTest.GetPoints();
        var points = allPoints.Where(p => p.Raw.Count > 0 && p.FloorId != null).ToList();

        // Points that never even enter the loop. Counting them and moving on is how wt9 - a walk
        // point that gets its floor wrong on every single tick - stayed invisible for a day: the
        // result said "1 skipped" and no more, and that number was not even shown in the UI.
        foreach (var p in allPoints.Except(points))
            result.Skipped.Add(new BenchmarkSkipped
            {
                Id = p.Id,
                FloorId = p.FloorId,
                Reason = p.FloorId == null
                    ? "No floor assigned, so there is nothing to score the estimate against."
                    : "No raw per-tick readings were stored with this point."
            });
        if (points.Count == 0)
        {
            result.Error = "No walk test points with raw tick data. Record a walk test first - the benchmark " +
                           "needs known positions to measure against, it cannot score the live stream.";
            return remember ? Remember(result) : result;
        }

        var nw = configLoader.Config?.Locators?.NadarayaWatson;
        result.Bandwidth = nw?.Bandwidth ?? 0.5;
        result.Kernel = nw?.Kernel ?? "gaussian";

        var allErrors = new List<double>();
        var perFloor = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var jitters = new List<double>();
        var roomHits = 0;
        var roomChecked = 0;
        var skippedNoData = 0;
        var recomputed = 0;
        var dropped = 0;
        var floorHits = 0;
        var floorChecked = 0;
        var floorHitPerFloor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var floorTotalPerFloor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var confusion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var point in points)
        {
            var truth = new Point3D(point.X, point.Y, point.Z);
            var floor = state.Floors.Values.FirstOrDefault(f => string.Equals(f.Id, point.FloorId, StringComparison.OrdinalIgnoreCase));
            var truthRoom = SpatialUtils.FindRoomContaining(truth, floor);

            var estimates = new List<Point3D>();
            var errors = new List<double>();
            var pointFloorHits = 0;
            var pointFloorChecked = 0;
            var bestOwnFloorHeard = 0;
            var bestOtherFloorHeard = 0;
            var ownFloorNodeCount = state.Nodes.Values.Count(n =>
                n.HasLocation && (n.Floors?.Any(f => string.Equals(f.Id, point.FloorId, StringComparison.OrdinalIgnoreCase)) ?? false));

            foreach (var tick in point.Raw.GroupBy(r => r.T))
            {
                var audible = new List<(Node node, double dist)>();
                foreach (var entry in tick)
                {
                    if (!state.Nodes.TryGetValue(entry.N, out var node) || !node.HasLocation) continue;
                    audible.Add((node, DistanceFor(entry, overrides, ref recomputed)));
                }

                // Scored on every audible node, deliberately BEFORE the same-floor cut below: that
                // cut is given the answer, so an error figure computed after it cannot say anything
                // about whether the floor would have been found in the first place.
                if (GuessFloor(audible, result.Bandwidth, result.Kernel, overrides?.FloorContrastWeight ?? 0) is { } guess)
                {
                    floorChecked++;
                    pointFloorChecked++;
                    if (string.Equals(guess, point.FloorId, StringComparison.OrdinalIgnoreCase))
                    {
                        floorHits++;
                        pointFloorHits++;
                        Bump(floorHitPerFloor, point.FloorId!);
                    }
                    else
                    {
                        Bump(confusion, $"{point.FloorId} -> {guess}");
                    }
                    Bump(floorTotalPerFloor, point.FloorId!);
                }

                var heard = audible
                    .Where(a => a.node.Floors?.Any(f => string.Equals(f.Id, point.FloorId, StringComparison.OrdinalIgnoreCase)) ?? false)
                    .Select(a => (loc: a.node.Location, dist: a.dist))
                    .ToList();
                bestOwnFloorHeard = Math.Max(bestOwnFloorHeard, heard.Count);
                bestOtherFloorHeard = Math.Max(bestOtherFloorHeard, audible.Count - heard.Count);
                if (heard.Count < MinNodesPerTick) continue;

                // Optional, so the effect can be measured before it is adopted live.
                if (overrides?.ConsistencyFilter == true)
                {
                    var kept = ConsistencyFilter.LargestConsistent(heard, h => h.loc, h => h.dist);
                    if (kept.Count >= MinNodesPerTick)
                    {
                        dropped += heard.Count - kept.Count;
                        heard = kept.ToList();
                    }
                }

                heard.Sort((a, b) => a.dist.CompareTo(b.dist));

                var (est, _) = NadarayaWatsonMultilateralizer.Estimate(heard, result.Bandwidth, result.Kernel);
                estimates.Add(est);

                // 2D only: Z is dominated by where the nodes happen to be mounted, not by how well
                // the locator works, and mixing it in would make the figure track ceiling heights.
                errors.Add(Math.Sqrt(Math.Pow(est.X - truth.X, 2) + Math.Pow(est.Y - truth.Y, 2)));

                if (truthRoom != null)
                {
                    roomChecked++;
                    if (SpatialUtils.FindRoomContaining(est, floor)?.Id == truthRoom.Id) roomHits++;
                }
            }

            if (estimates.Count < MinTicksPerPoint)
            {
                skippedNoData++;
                // Spelled out, because "skipped" alone reads as "nothing to see here" while the
                // actual situation is often the most informative measurement on the installation:
                // a spot where the device is loudly heard, just not by the floor it is standing on.
                var reason = bestOwnFloorHeard < MinNodesPerTick
                    ? $"Only {bestOwnFloorHeard} of the {ownFloorNodeCount} nodes on its own floor ever heard it, " +
                      $"and a position needs {MinNodesPerTick}." +
                      (bestOtherFloorHeard >= MinNodesPerTick
                          ? $" {bestOtherFloorHeard} nodes on other floors did hear it - so the signal is there, " +
                            "it just belongs to the wrong storey. Another node on this floor would fix it."
                          : " Nothing else heard it either, so this spot has no coverage at all.")
                    : $"Only {estimates.Count} usable ticks, and {MinTicksPerPoint} are needed - a shorter " +
                      "reading than this says more about luck than accuracy. Record it again for longer.";

                result.Skipped.Add(new BenchmarkSkipped
                {
                    Id = point.Id,
                    FloorId = point.FloorId,
                    RoomName = truthRoom?.Name,
                    OwnFloorNodesHeard = bestOwnFloorHeard,
                    OwnFloorNodeCount = ownFloorNodeCount,
                    OtherFloorNodesHeard = bestOtherFloorHeard,
                    Reason = reason
                });
                continue;
            }

            allErrors.AddRange(errors);
            if (point.FloorId != null)
            {
                if (!perFloor.TryGetValue(point.FloorId, out var list)) perFloor[point.FloorId] = list = new List<double>();
                list.AddRange(errors);
            }

            var cx = estimates.Average(e => e.X);
            var cy = estimates.Average(e => e.Y);
            jitters.Add(Math.Sqrt(estimates.Average(e => Math.Pow(e.X - cx, 2) + Math.Pow(e.Y - cy, 2))));

            result.Points.Add(new BenchmarkPoint
            {
                Id = point.Id,
                FloorId = point.FloorId,
                RoomName = truthRoom?.Name,
                Ticks = estimates.Count,
                MedianErrorM = Round(Median(errors)),
                P90ErrorM = Round(Percentile(errors, 0.90)),
                // Per point, not just per floor: "88 % floor" says nothing about WHERE it fails, and
                // the two candidate explanations - a stairwell being genuinely ambiguous versus a
                // floor being under-covered - call for completely different responses.
                FloorHitRate = pointFloorChecked > 0 ? Math.Round((double)pointFloorHits / pointFloorChecked, 3) : null
            });
        }

        if (allErrors.Count == 0)
        {
            result.Error = $"None of the {points.Count} walk points produced an estimate - each tick needs at least " +
                           $"{MinNodesPerTick} nodes on the point's own floor. Check that the floor assignments are right.";
            return remember ? Remember(result) : result;
        }

        result.PointsUsed = result.Points.Count;
        // Points recorded before per-tick levels existed can only score the locator: their ticks
        // carry the distance the node already derived, so absorption, reference level and receive
        // adjustment are baked in and invisible to a replay. Reported rather than silently mixed,
        // because a run whose mix has shifted is not comparable with the previous one.
        result.PointsWithLevels = points.Count(p => p.SupportsCalibrationReplay);
        result.RecomputedTicks = recomputed;
        result.DroppedInconsistent = dropped;
        result.PointsSkipped = skippedNoData;
        result.Ticks = allErrors.Count;
        result.MedianErrorM = Round(Median(allErrors));
        result.P90ErrorM = Round(Percentile(allErrors, 0.90));
        result.MeanErrorM = Round(allErrors.Average());
        result.MeanJitterM = jitters.Count > 0 ? Round(jitters.Average()) : null;
        result.RoomHitRate = roomChecked > 0 ? Math.Round((double)roomHits / roomChecked, 3) : null;
        result.FloorTicksChecked = floorChecked;
        result.FloorHitRate = floorChecked > 0 ? Math.Round((double)floorHits / floorChecked, 3) : null;
        result.FloorConfusion = confusion
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new BenchmarkConfusion { Pair = kv.Key, Ticks = kv.Value })
            .ToList();

        foreach (var (floorId, errs) in perFloor)
        {
            floorTotalPerFloor.TryGetValue(floorId, out var fTotal);
            floorHitPerFloor.TryGetValue(floorId, out var fHit);
            result.Floors.Add(new BenchmarkFloor
            {
                FloorId = floorId,
                Ticks = errs.Count,
                MedianErrorM = Round(Median(errs)),
                P90ErrorM = Round(Percentile(errs, 0.90)),
                FloorHitRate = fTotal > 0 ? Math.Round((double)fHit / fTotal, 3) : null
            });
        }
        result.Floors = result.Floors.OrderByDescending(f => f.MedianErrorM).ToList();

        // Only against a run replayed with the SAME what-if parameters. Observed 2026-07-26: a plain
        // run was reported as "12 cm worse" than a predecessor replayed with refRssi -77 and the
        // consistency filter on - two different questions, and the difference between the answers is
        // not a change in accuracy. Comparing across override sets makes the yardstick lie in exactly
        // the situation it exists for, namely deciding whether a change helped.
        var signature = Signature(overrides);
        var previous = _history.LastOrDefault(r => r.Error == null && Signature(r.Overrides) == signature);
        if (previous?.MedianErrorM is { } before && result.MedianErrorM is { } now)
        {
            result.DeltaMedianM = Round(now - before);
            // Plain language on purpose: "0.51" means nothing to someone setting this up for the
            // first time, "20 cm worse than last run" does.
            var cm = Math.Abs((now - before) * 100);
            result.Verdict = Math.Abs(now - before) < 0.05
                ? $"Unchanged within noise (median {now:0.00} m)."
                : now < before
                    ? $"Better: median {now:0.00} m, {cm:0} cm closer than the previous run."
                    : $"Worse: median {now:0.00} m, {cm:0} cm further off than the previous run.";
        }
        else
        {
            var scope = overrides == null ? "" : " for these replay settings";
            result.Verdict = $"Baseline{scope}: median {result.MedianErrorM:0.00} m, 90th percentile " +
                             $"{result.P90ErrorM:0.00} m over {result.Ticks} ticks from {result.PointsUsed} points.";
        }

        // Appended rather than folded in: the distance figures above are all measured on the correct
        // floor, so they say nothing about this. A run can improve by centimetres while sending the
        // device to the wrong storey, and that is the failure a resident actually notices.
        if (result.FloorHitRate is { } fhr)
        {
            result.Verdict += fhr >= 0.95
                ? $" Floor found on {fhr:P0} of ticks."
                : $" Floor found on only {fhr:P0} of ticks - the room and distance figures above are " +
                  "measured on the correct floor and do not reflect this.";
            var worst = result.FloorConfusion.FirstOrDefault();
            if (worst != null && fhr < 0.95) result.Verdict += $" Most common mix-up: {worst.Pair}.";
        }

        Log.Information("Benchmark: median {Median:0.00} m, p90 {P90:0.00} m, room hit {Hit:P0}, {Ticks} ticks",
            result.MedianErrorM, result.P90ErrorM, result.RoomHitRate ?? 0, result.Ticks);

        return remember ? Remember(result) : result;
    }

    private BenchmarkResult Remember(BenchmarkResult result)
    {
        _history.Add(result);
        while (_history.Count > MaxHistory) _history.RemoveAt(0);
        Save();
        return result;
    }

    /// <summary>
    /// Distance for one recorded reading. Without overrides, or on a point recorded before levels
    /// were stored, this is simply what the node reported at the time.
    ///
    /// With overrides it is recomputed from the stored level, which is the only way to score a
    /// calibration change at all: the recorded distance is a DERIVED value the node produced with
    /// the reference level and absorption in force back then, so replaying it can only ever
    /// reproduce that state. The node's formula is
    /// <c>d = 10^((refRssi - rssi) / (10 * absorption))</c>, and since the tick carries refRssi,
    /// rssi and the resulting distance, the absorption it used can be recovered exactly - no
    /// assumption needed, and nothing extra had to be stored.
    /// </summary>
    private static double DistanceFor(WalkTestService.RawTickEntry e, BenchmarkOverrides? o, ref int recomputed)
    {
        if (o == null || e.R is not { } rssi || e.Ref is not { } recordedRef || e.D <= 0) return e.D;

        // Recover the absorption the node used. At exactly 1 m log10(d) is zero and it is
        // undetermined - fall back rather than divide by zero.
        var logD = Math.Log10(e.D);
        if (Math.Abs(logD) < 1e-6) return e.D;
        var recordedAbsorption = (recordedRef - rssi) / (10.0 * logD);
        if (recordedAbsorption is <= 0.1 or > 10) return e.D;   // implausible, do not build on it

        // Per-node values win over the global one. Without this the benchmark can only score "what
        // if every node had the same absorption", which is not a question the optimizer ever
        // answers - it fits each node separately, so scoring its output needs a whole set.
        var absorption = o.AbsorptionFor(e.N) ?? recordedAbsorption;
        var refRssi = o.RefRssi ?? recordedRef;

        // Receive adjustment shifts the level, not the distance. Applied the same way the live path
        // does it (see Measure.GetAdjustedRssi): the recording carries the adjustment in force at
        // the time, so only the difference to the candidate value is applied.
        if (o.RxAdjFor(e.N) is { } newRxAdj && e.A is { } recordedRxAdj)
            rssi = rssi + recordedRxAdj - newRxAdj;

        recomputed++;
        return Math.Pow(10, (refRssi - rssi) / (10.0 * absorption));
    }

    /// <summary>Identifies the question a run was asking, so only like is compared with like.</summary>
    private static string Signature(BenchmarkOverrides? o)
    {
        if (o == null) return "as-recorded";
        // Per-node sets have to enter the signature too, otherwise two different calibration sets
        // look like the same question and get compared against each other as if one were a change
        // over time. Order-independent so the same set always hashes the same way.
        static string Set(Dictionary<string, double>? d) => d == null || d.Count == 0
            ? ""
            : string.Join(",", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value:0.###}"));
        return $"{o.RefRssi}|{o.Absorption}|{o.ConsistencyFilter}|{o.FloorContrastWeight}|{Set(o.AbsorptionByNode)}|{Set(o.RxAdjByNode)}";
    }

    private static void Bump(Dictionary<string, int> counter, string key)
        => counter[key] = counter.TryGetValue(key, out var n) ? n + 1 : 1;

    /// <summary>
    /// Which floor would the live locator have landed on for this tick?
    ///
    /// Floor detection is not a separate algorithm in ESPresense - <see cref="State.GetScenarios"/>
    /// builds one scenario PER FLOOR and <see cref="MultiScenarioLocator"/> publishes whichever wins
    /// on confidence. So scoring it means doing exactly that: fit every floor from the nodes on it
    /// and see which one comes out ahead. The arithmetic below mirrors
    /// <see cref="NadarayaWatsonMultilateralizer.Locate"/> including its two-node branch, because
    /// that branch exists precisely to keep sparsely-covered floors competitive - skipping it here
    /// would score a system that is not the one running.
    ///
    /// One honest approximation: the live code counts only nodes currently ONLINE as the coverage
    /// denominator, and a recording does not say who was online at the time. Nodes with a position
    /// are counted instead, which understates confidence on a floor that had a node down. It shifts
    /// both floors in the same direction, so comparisons between runs stay sound.
    /// </summary>
    private string? GuessFloor(IReadOnlyList<(Node node, double dist)> audible, double bandwidth, string? kernel, double contrastWeight)
    {
        string? best = null;
        var bestConfidence = 0;

        foreach (var floor in state.Floors.Values)
        {
            var heard = audible.Where(a => a.node.Floors?.Contains(floor) ?? false)
                               .OrderBy(a => a.dist)
                               .ToList();
            if (heard.Count <= 1) continue;

            Point3D est;
            double error;
            double? pearson;

            if (heard.Count < 3 || floor.Bounds == null)
            {
                est = Point3D.MidPoint(heard[0].node.Location, heard[1].node.Location);
                error = heard.Average(n => Math.Pow(est.DistanceTo(n.node.Location) - n.dist, 2));
                pearson = null;
            }
            else
            {
                (est, error) = NadarayaWatsonMultilateralizer.Estimate(
                    heard.Select(n => (n.node.Location, n.dist)).ToList(), bandwidth, kernel);
                pearson = MathUtils.CalculatePearsonCorrelation(
                    heard.Select(n => n.dist).ToList(),
                    heard.Select(n => est.DistanceTo(n.node.Location)).ToList());
            }

            var possible = state.Nodes.Values.Count(n => (n.Floors?.Contains(floor) ?? false) && n.HasLocation);
            var confidence = MathUtils.CalculateConfidence(error, pearson, heard.Count, possible);

            // The readings every floor scenario throws away, used to tell the storeys apart rather
            // than to place the device. See FloorContrast for the physics and the measurements.
            confidence += (int)Math.Round(FloorContrast.Adjustment(
                est, audible, r => r.node.Location, r => r.dist,
                r => r.node.Floors?.Contains(floor) ?? false, contrastWeight));

            if (confidence > bestConfidence)
            {
                bestConfidence = confidence;
                best = floor.Id;
            }
        }

        return best;
    }

    private static double? Round(double v) => Math.Round(v, 2);

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }

    private static double Percentile(List<double> values, double p)
    {
        var s = values.OrderBy(v => v).ToList();
        if (s.Count == 1) return s[0];
        var rank = p * (s.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return lo == hi ? s[lo] : s[lo] + (rank - lo) * (s[hi] - s[lo]);
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(persistPath)) return;
        try { File.WriteAllText(persistPath, JsonConvert.SerializeObject(_history)); }
        catch (Exception ex) { Log.Warning(ex, "Could not persist benchmark history to {Path}", persistPath); }
    }

    private static List<BenchmarkResult> Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<BenchmarkResult>();
        try { return JsonConvert.DeserializeObject<List<BenchmarkResult>>(File.ReadAllText(path)) ?? new List<BenchmarkResult>(); }
        catch (Exception ex) { Log.Warning(ex, "Could not read benchmark history from {Path}", path); return new List<BenchmarkResult>(); }
    }
}

public class BenchmarkResult
{
    public DateTime RanAt { get; set; }
    public string? Label { get; set; }
    public string? Error { get; set; }

    public double Bandwidth { get; set; }
    public string Kernel { get; set; } = "";

    public int PointsUsed { get; set; }
    public int PointsSkipped { get; set; }
    /// <summary>Of the used points, how many carry per-tick signal levels - only those can score
    /// calibration changes rather than just the locator.</summary>
    public int PointsWithLevels { get; set; }
    public int Ticks { get; set; }

    public double? MedianErrorM { get; set; }
    public double? P90ErrorM { get; set; }
    public double? MeanErrorM { get; set; }
    public double? MeanJitterM { get; set; }
    /// <summary>Fraction of ticks placed in the same room as the ground truth - what presence automations actually depend on.</summary>
    public double? RoomHitRate { get; set; }

    /// <summary>Fraction of ticks whose winning floor scenario was the floor the point is actually
    /// on. Scored across all audible nodes, unlike every distance figure here, which is measured
    /// with the floor already known.</summary>
    public double? FloorHitRate { get; set; }
    public int FloorTicksChecked { get; set; }
    /// <summary>The most frequent wrong answers, as "true -> guessed".</summary>
    public List<BenchmarkConfusion> FloorConfusion { get; set; } = new();

    /// <summary>Parameters this run was replayed with, null when it measured the state as recorded.</summary>
    public BenchmarkOverrides? Overrides { get; set; }
    /// <summary>Readings whose distance was recomputed from the stored level.</summary>
    public int RecomputedTicks { get; set; }
    /// <summary>Readings discarded as geometrically impossible alongside the others.</summary>
    public int DroppedInconsistent { get; set; }

    public double? DeltaMedianM { get; set; }
    public string? Verdict { get; set; }

    /// <summary>Points left out of the score, each with the reason. See <see cref="BenchmarkSkipped"/>.</summary>
    public List<BenchmarkSkipped> Skipped { get; set; } = new();

    public List<BenchmarkFloor> Floors { get; set; } = new();
    public List<BenchmarkPoint> Points { get; set; } = new();
}

public class BenchmarkFloor
{
    public string FloorId { get; set; } = "";
    public int Ticks { get; set; }
    public double? MedianErrorM { get; set; }
    public double? P90ErrorM { get; set; }
    /// <summary>How often ticks truly on this floor were assigned to it.</summary>
    public double? FloorHitRate { get; set; }
}

/// <summary>
/// A walk point the benchmark could not score, and why. Exists because the count on its own was
/// actively misleading: the one point it hid turned out to be the clearest evidence on the whole
/// installation that some spots are heard by the wrong floor's nodes.
/// </summary>
public class BenchmarkSkipped
{
    public string Id { get; set; } = "";
    public string? FloorId { get; set; }
    public string? RoomName { get; set; }
    /// <summary>Most nodes on the point's own floor that were heard in any single tick.</summary>
    public int OwnFloorNodesHeard { get; set; }
    public int OwnFloorNodeCount { get; set; }
    /// <summary>Most nodes on OTHER floors heard in any single tick - the information being discarded.</summary>
    public int OtherFloorNodesHeard { get; set; }
    public string Reason { get; set; } = "";
}

public class BenchmarkConfusion
{
    public string Pair { get; set; } = "";
    public int Ticks { get; set; }
}

public class BenchmarkPoint
{
    public string Id { get; set; } = "";
    public string? FloorId { get; set; }
    public string? RoomName { get; set; }
    public int Ticks { get; set; }
    public double? MedianErrorM { get; set; }
    public double? P90ErrorM { get; set; }
    /// <summary>How often this spot was assigned to the floor it is actually on.</summary>
    public double? FloorHitRate { get; set; }
}

/// <summary>
/// What-if parameters for a replay. Leaving a field null keeps the value the recording was made
/// with, so a run with everything null reproduces the recorded state and can be used to verify the
/// recomputation itself before trusting any comparison built on it.
/// </summary>
public class BenchmarkOverrides
{
    /// <summary>Device reference level to replay with, in dBm.</summary>
    public double? RefRssi { get; set; }
    /// <summary>
    /// Per-node absorption, keyed by node id - what an optimizer run actually produces. Takes
    /// precedence over the global <see cref="Absorption"/> for the nodes it names.
    /// </summary>
    public Dictionary<string, double>? AbsorptionByNode { get; set; }

    /// <summary>
    /// How many confidence points the cross-floor contrast may add or subtract. 0 disables it, which
    /// is the default until a measurement says otherwise.
    /// </summary>
    public double? FloorContrastWeight { get; set; }

    /// <summary>Per-node receive adjustment, keyed by node id.</summary>
    public Dictionary<string, double>? RxAdjByNode { get; set; }

    public double? AbsorptionFor(string nodeId) =>
        AbsorptionByNode != null && AbsorptionByNode.TryGetValue(nodeId, out var a) ? a : Absorption;

    public double? RxAdjFor(string nodeId) =>
        RxAdjByNode != null && RxAdjByNode.TryGetValue(nodeId, out var a) ? a : null;

    /// <summary>Path-loss exponent to replay with, replacing whatever each node used.</summary>
    public double? Absorption { get; set; }
    /// <summary>Drop readings that contradict the others through the triangle inequality.</summary>
    public bool? ConsistencyFilter { get; set; }
}
