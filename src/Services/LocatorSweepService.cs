using ESPresense.Locators;
using ESPresense.Utils;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Works out which locators should be enabled, by replaying the recorded walk points through the
/// REAL scenario competition rather than through a reimplementation of one estimator.
///
/// Why this had to exist: the locator selection in the running configuration was mine, chosen by
/// judgement and never measured. Four multilateralizers were enabled at once, and since
/// <see cref="State.GetScenarios"/> builds one scenario per locator AND per floor, six of them were
/// competing at a single spot. On 2026-07-27 that showed up as tracking flipping between the right
/// room and a stairwell one floor down for minutes at a time.
///
/// ★ And <see cref="CalibrationBenchmark"/> could not have caught it. It replays Nadaraya-Watson
/// arithmetic directly, so it measures one of four competing paths and reports the result as if it
/// were the system - 99.8 % floor accuracy for a spot the live system got right 46 % of the time.
/// A yardstick narrower than its subject is worse than none, because it is trusted.
///
/// So this service constructs a real <see cref="Device"/> per recorded tick, hands it to the actual
/// locator classes, and lets them compete exactly as they do live. Slower, and the only way the
/// answer means anything.
/// </summary>
public class LocatorSweepService(State state, WalkTestService walkTest, ConfigLoader configLoader, ScenarioReplay replay)
{
    // Schwellen und Szenarien-Wettbewerb kommen aus ScenarioReplay - genau derselbe Massstab,
    // den Benchmark und Locator-Tuning benutzen.
    private const int MinNodesPerTick = ScenarioReplay.MinNodesPerTick;

    private const int MinTicksPerPoint = ScenarioReplay.MinTicksPerPoint;

    public LocatorSweepResult Run(LocatorSweepRequest? request = null)
    {
        var result = new LocatorSweepResult { RanAt = DateTime.UtcNow };
        var points = walkTest.GetPoints().Where(p => p.Raw.Count > 0 && p.FloorId != null).ToList();
        if (points.Count == 0)
        {
            result.Error = "No walk test points with raw tick data - there is nothing to score against.";
            return result;
        }

        var floors = state.Floors.Values.Where(f => f.Id != null).ToList();
        if (floors.Count == 0)
        {
            result.Error = "No floors in the configuration.";
            return result;
        }

        var candidates = request?.Candidates is { Count: > 0 } supplied ? supplied : DefaultCandidates();
        var contrastWeight = request?.FloorContrastWeight ?? configLoader.Config?.Locators?.FloorContrastWeight ?? 0;
        result.FloorContrastWeightUsed = contrastWeight;

        foreach (var candidate in candidates)
        {
            try { result.Runs.Add(Score(candidate, points, floors, contrastWeight)); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Locator sweep candidate {Label} failed", candidate.Label);
                result.Runs.Add(new LocatorSweepRun { Label = candidate.Label, Error = ex.Message });
            }
        }

        result.Runs = result.Runs.OrderByDescending(r => r.RoomHitRate ?? -1).ToList();
        result.Recommendation = Recommend(result);
        return result;
    }

    /// <summary>Standard error across walk points, or null when there are too few to say.</summary>
    private static double? StandardError(List<double> perPoint)
    {
        if (perPoint.Count < 2) return null;
        var mean = perPoint.Average();
        var variance = perPoint.Sum(v => (v - mean) * (v - mean)) / (perPoint.Count - 1);
        return Math.Round(Math.Sqrt(variance / perPoint.Count), 3);
    }

    /// <summary>
    /// Picks a combination and says why, instead of handing the user a table and a shrug.
    ///
    /// The order of questions matters more than any single number, so it is spelled out:
    ///
    /// 1. ROOM first, because that is what presence automations consume - and it already contains
    ///    the rest. A scenario on the wrong floor cannot name the right room, and neither can one
    ///    that is half a room off. Scoring position, room and floor separately, as an earlier version
    ///    did, mostly measured the same thing three times and then called the disagreement a
    ///    trade-off for the user to resolve.
    ///
    /// 2. FLOOR when room cannot separate the candidates. Measured on this installation the room
    ///    rates span 48-54 % with an uncertainty of +/-8 % across points - every candidate ties, so
    ///    ranking on it would be picking noise. The floor rates over the same points span 84-98 %
    ///    with far less scatter, and they do separate. A wrong storey is also the error a resident
    ///    actually notices, so falling back to it is not a consolation prize.
    ///
    /// 3. MEDIAN POSITION ERROR when room and floor both tie. Added 2026-07-27 after the rule picked
    ///    the horizontally WORST candidate on this installation (2.40 m against 1.95 m) purely on
    ///    floor, and said nothing about it - the user saw a 2.5 m drift on the map and the wizard
    ///    reported everything as fine. Metres are not a consolation prize either: two candidates that
    ///    name the same room equally often still differ in where inside it they put the device.
    ///
    /// 4. FEWEST locators when nothing separates anything. Less computation, one less thing to
    ///    explain, and no evidence to justify paying for extras.
    ///
    /// Whatever decides, the trade against the best alternative is NAMED (see the closing sentence
    /// built below) rather than silently taken.
    ///
    /// Uncertainty is computed across walk POINTS, never across ticks. Thousands of ticks sound like
    /// a large sample, but the ticks inside one point are the same device standing in one place - the
    /// real sample size is how many spots it was put in, here 32.
    /// </summary>
    private static LocatorRecommendation? Recommend(LocatorSweepResult result)
    {
        var usable = result.Runs.Where(r => r.Error == null && r.RoomHitRate.HasValue).ToList();
        if (usable.Count == 0) return null;

        static List<LocatorSweepRun> WithinNoise(List<LocatorSweepRun> runs, Func<LocatorSweepRun, double> value,
            Func<LocatorSweepRun, double?> error)
        {
            var leader = runs.MaxBy(value)!;
            var margin = error(leader) ?? 0;
            return runs.Where(r => value(leader) - value(r) <= margin).ToList();
        }

        var roomTied = WithinNoise(usable, r => r.RoomHitRate ?? 0, r => r.RoomHitStandardErrorPoints);
        var decidedBy = "room";
        var field = roomTied;

        if (roomTied.Count > 1)
        {
            var floorTied = WithinNoise(roomTied, r => r.FloorHitRate ?? 0, r => r.FloorHitStandardErrorPoints);
            if (floorTied.Count < roomTied.Count) { field = floorTied; decidedBy = "floor"; }
            else
            {
                // Raum und Etage trennen beide nicht - dann entscheiden die Meter. Negiert, weil
                // WithinNoise "groesser ist besser" erwartet und beim Fehler das Gegenteil gilt.
                var medianTied = WithinNoise(floorTied, r => -(r.MedianErrorM ?? 0), r => r.MedianErrorStandardErrorM);
                if (medianTied.Count < floorTied.Count) { field = medianTied; decidedBy = "position"; }
                else { field = medianTied; decidedBy = "simplicity"; }
            }
        }

        var pick = field
            .OrderBy(r => r.Locators.Count)
            .ThenBy(r => r.MedianErrorM ?? double.MaxValue)
            .ThenByDescending(r => r.FloorHitRate ?? 0)
            .ThenByDescending(r => r.RoomHitRate ?? 0)
            .First();

        var roomMargin = usable.MaxBy(r => r.RoomHitRate)!.RoomHitStandardErrorPoints ?? 0;
        var reason = decidedBy switch
        {
            "room" =>
                $"'{pick.Label}' puts the device in the right room {pick.RoomHitRate:P0} of the time, more than any " +
                $"other combination by more than the measurement's own scatter. Right-room rate is what presence " +
                $"automations consume, and a wrong floor or a badly wrong position both already show up in it.",
            "floor" =>
                $"Every combination lands in the right room about equally often ({pick.RoomHitRate:P0} here, and the " +
                $"spread across all of them is inside the ±{roomMargin:P0} uncertainty across {pick.PointsUsed} walk " +
                $"points), so that figure cannot choose. The floor does separate them: '{pick.Label}' gets the storey " +
                $"right {pick.FloorHitRate:P0} of the time, and being on the wrong floor is the error you actually notice.",
            "position" =>
                $"Right-room rates tie (within ±{roomMargin:P0} across {pick.PointsUsed} walk points) and so do the " +
                $"floor rates, so neither can choose. The distances do: '{pick.Label}' lands a median " +
                $"{pick.MedianErrorM:0.00} m from the truth, further than the scatter of the measurement itself from " +
                $"any other candidate. Same room, but closer to the right spot inside it.",
            _ =>
                $"Nothing measurable separates the candidates - right-room rates all sit within ±{roomMargin:P0} of " +
                $"each other across {pick.PointsUsed} walk points, the floor rates agree too, and so do the median " +
                $"position errors. '{pick.Label}' is recommended because it achieves that with the fewest estimators, " +
                $"which costs the least computation and leaves less to go wrong."
        };

        // ★ Den Handel benennen statt ihn stillschweigend einzugehen. Entscheidet die Etage, zahlt man
        // dafuer oft in Metern - auf dieser Anlage 0,45 m, und niemand sah es. Wer die Empfehlung
        // annimmt, soll wissen, was er dafuer aufgibt.
        var bestMedian = usable.Where(r => r.MedianErrorM.HasValue).MinBy(r => r.MedianErrorM);
        if (bestMedian != null && pick.MedianErrorM is { } pickMedian && bestMedian.MedianErrorM is { } bestM
            && !bestMedian.Locators.ToHashSet().SetEquals(pick.Locators) && pickMedian - bestM >= 0.1)
            reason += $" The trade: '{bestMedian.Label}' is {pickMedian - bestM:0.00} m better on median position " +
                      $"error ({bestM:0.00} m against {pickMedian:0.00} m), but gets the storey right " +
                      $"{bestMedian.FloorHitRate:P0} of the time instead of {pick.FloorHitRate:P0}.";

        var current = usable.FirstOrDefault(r => r.IsCurrentConfiguration);
        if (current != null && !current.Locators.ToHashSet().SetEquals(pick.Locators))
        {
            var dropped = current.Locators.Except(pick.Locators).ToList();
            var added = pick.Locators.Except(current.Locators).ToList();
            if (dropped.Count > 0)
                reason += $" Against the current configuration this drops {string.Join(" and ", dropped)}";
            if (added.Count > 0)
                reason += (dropped.Count > 0 ? " and adds " : " Against the current configuration this adds ")
                          + string.Join(" and ", added);
            if (dropped.Count > 0 || added.Count > 0) reason += ".";
        }

        return new LocatorRecommendation
        {
            Locators = pick.Locators.ToList(),
            Label = pick.Label,
            RoomHitRate = pick.RoomHitRate,
            MedianErrorM = pick.MedianErrorM,
            FloorHitRate = pick.FloorHitRate,
            DecidedBy = decidedBy,
            // Vergleich ueber die Locator-MENGE, nicht ueber Objektidentitaet: zwei Kandidaten mit
            // demselben Satz sind dasselbe Ergebnis, egal ob es dieselbe Instanz ist und in welcher
            // Reihenfolge die Namen stehen.
            AlreadyConfigured = current != null && current.Locators.ToHashSet().SetEquals(pick.Locators),
            Reason = reason
        };
    }

    private LocatorSweepRun Score(LocatorCandidate candidate, List<WalkTestService.WalkTestPoint> points,
        List<Floor> floors, double contrastWeight)
    {
        var run = new LocatorSweepRun
        {
            Label = candidate.Label,
            Locators = candidate.Locators.ToList(),
            IsCurrentConfiguration = candidate.IsCurrentConfiguration
        };

        var errors = new List<double>();
        var floorHits = 0;
        var floorChecked = 0;
        var roomHits = 0;
        var roomChecked = 0;
        var pointsUsed = 0;

        foreach (var point in points)
        {
            var truth = new Point3D(point.X, point.Y, point.Z);
            var truthFloor = floors.FirstOrDefault(f => string.Equals(f.Id, point.FloorId, StringComparison.OrdinalIgnoreCase));
            var truthRoom = SpatialUtils.FindRoomContaining(truth, truthFloor);
            var scored = 0;
            var pointErrors = new List<double>();
            var pointRoomHits = 0;
            var pointRoomChecked = 0;
            var pointFloorHits = 0;
            var pointFloorChecked = 0;

            foreach (var tick in point.Raw.GroupBy(r => r.T))
            {
                var readings = tick
                    .Select(e => state.Nodes.TryGetValue(e.N, out var n) && n.HasLocation ? (node: n, dist: e.D, var: e.V) : default)
                    .Where(r => r.node != null && r.dist > 0)
                    .ToList();
                if (readings.Count < MinNodesPerTick) continue;

                var best = replay.BestScenario(readings, floors, new ScenarioReplay.Options
                {
                    Locators = candidate.Locators, ContrastWeight = contrastWeight
                });
                if (best == null) continue;

                scored++;
                var err = ScenarioReplay.Error2D(best.Location, truth);
                errors.Add(err);
                pointErrors.Add(err);

                floorChecked++;
                pointFloorChecked++;
                if (string.Equals(best.Floor?.Id, point.FloorId, StringComparison.OrdinalIgnoreCase))
                { floorHits++; pointFloorHits++; }

                if (truthRoom != null)
                {
                    roomChecked++;
                    pointRoomChecked++;
                    if (best.Room?.Id == truthRoom.Id) { roomHits++; pointRoomHits++; }
                }
            }

            if (scored >= MinTicksPerPoint) pointsUsed++;
            // Per POINT, not per tick. Ticks inside one point are the same device standing in the
            // same spot, so counting them as independent makes any difference look far more certain
            // than it is - thousands of ticks, but only as many real observations as there are places
            // the device was actually put.
            if (pointErrors.Count > 0)
            {
                pointErrors.Sort();
                run.PerPointMedianErrorM.Add(pointErrors[pointErrors.Count / 2]);
            }
            if (pointRoomChecked > 0) run.PerPointRoomHitRate.Add((double)pointRoomHits / pointRoomChecked);
            if (pointFloorChecked > 0) run.PerPointFloorHitRate.Add((double)pointFloorHits / pointFloorChecked);
        }

        if (errors.Count == 0)
        {
            run.Error = "No tick produced a position with these locators.";
            return run;
        }

        errors.Sort();
        run.Ticks = errors.Count;
        run.PointsUsed = pointsUsed;
        run.MedianErrorM = Math.Round(errors[errors.Count / 2], 2);
        run.P90ErrorM = Math.Round(errors[(int)(0.9 * (errors.Count - 1))], 2);
        run.FloorHitRate = floorChecked > 0 ? Math.Round((double)floorHits / floorChecked, 3) : null;
        run.RoomHitRate = roomChecked > 0 ? Math.Round((double)roomHits / roomChecked, 3) : null;
        run.RoomHitStandardErrorPoints = StandardError(run.PerPointRoomHitRate);
        run.FloorHitStandardErrorPoints = StandardError(run.PerPointFloorHitRate);
        run.MedianErrorStandardErrorM = StandardError(run.PerPointMedianErrorM);
        return run;
    }

    /// <summary>
    /// Every locator on its own, then the combination currently configured. Alone first because that
    /// is the question nobody had asked: enabling four is only right if four together beat the best
    /// one, and "more estimators must be better" is an assumption, not a measurement.
    /// </summary>
    private List<LocatorCandidate> DefaultCandidates()
    {
        var l = configLoader.Config?.Locators;
        var configured = new List<string>();
        if (l?.NadarayaWatson?.Enabled ?? false) configured.Add("nadaraya_watson");
        if (l?.NelderMead?.Enabled ?? false) configured.Add("nelder_mead");
        if (l?.Mle?.Enabled ?? false) configured.Add("mle");
        if (l?.Bfgs?.Enabled ?? false) configured.Add("bfgs");
        if (l?.NearestNode?.Enabled ?? false) configured.Add("nearest_node");

        var candidates = new List<LocatorCandidate>
        {
            new() { Label = "nadaraya_watson alone", Locators = { "nadaraya_watson" } },
            new() { Label = "nelder_mead alone", Locators = { "nelder_mead" } },
            new() { Label = "mle alone", Locators = { "mle" } },
            new() { Label = "nadaraya_watson + nelder_mead", Locators = { "nadaraya_watson", "nelder_mead" } },
            new() { Label = "nadaraya_watson + mle", Locators = { "nadaraya_watson", "mle" } }
        };

        // Mark the configured combination rather than appending it a second time. It used to be added
        // unconditionally, so a configuration that already matched one of the fixed candidates was
        // swept twice as two distinct objects with identical scores. The tie then went to the fixed
        // entry, and the ReferenceEquals check in Recommend() compared object identity - so the very
        // case where nothing needs changing reported "not configured yet" and offered an Apply button
        // for the settings already in force. Marking in place also saves a full sweep run and keeps
        // the combination from showing up twice in the results table.
        if (configured.Count > 0)
        {
            var existing = candidates.FirstOrDefault(c => c.Locators.ToHashSet().SetEquals(configured));
            if (existing != null)
            {
                existing.IsCurrentConfiguration = true;
                existing.Label += " (as configured)";
            }
            else
                candidates.Add(new LocatorCandidate
                {
                    Label = $"as configured ({string.Join(" + ", configured)})",
                    Locators = configured,
                    IsCurrentConfiguration = true
                });
        }

        return candidates;
    }
}

public class LocatorSweepRequest
{
    public List<LocatorCandidate>? Candidates { get; set; }
    /// <summary>Held constant across candidates unless given, so only the locator choice varies.</summary>
    public double? FloorContrastWeight { get; set; }
}

public class LocatorCandidate
{
    public string Label { get; set; } = "";
    /// <summary>Ids as they appear in config.yaml: nadaraya_watson, nelder_mead, mle, bfgs, nearest_node.</summary>
    public List<string> Locators { get; set; } = new();
    public bool IsCurrentConfiguration { get; set; }
}

public class LocatorSweepResult
{
    public DateTime RanAt { get; set; }
    public string? Error { get; set; }
    /// <summary>What the wizard advises, and why. Null only when nothing could be scored.</summary>
    public LocatorRecommendation? Recommendation { get; set; }
    public double FloorContrastWeightUsed { get; set; }
    public List<LocatorSweepRun> Runs { get; set; } = new();
}

public class LocatorRecommendation
{
    public List<string> Locators { get; set; } = new();
    public string Label { get; set; } = "";
    public string Reason { get; set; } = "";
    public double? RoomHitRate { get; set; }
    public double? MedianErrorM { get; set; }
    public double? FloorHitRate { get; set; }
    public bool AlreadyConfigured { get; set; }
    /// <summary>Which question actually decided it: room, floor, or simplicity.</summary>
    public string DecidedBy { get; set; } = "";
}

public class LocatorSweepRun
{
    public string Label { get; set; } = "";
    public List<string> Locators { get; set; } = new();
    public bool IsCurrentConfiguration { get; set; }
    public string? Error { get; set; }
    public int Ticks { get; set; }
    public int PointsUsed { get; set; }
    public double? MedianErrorM { get; set; }
    public double? P90ErrorM { get; set; }
    public double? RoomHitRate { get; set; }
    public double? FloorHitRate { get; set; }

    /// <summary>Standard error of the room hit rate across walk POINTS - the honest uncertainty.</summary>
    public double? RoomHitStandardErrorPoints { get; set; }

    public double? FloorHitStandardErrorPoints { get; set; }

    /// <summary>
    /// Standard error of the median position error across walk POINTS, on the same footing as the two
    /// hit rates - so "is this half metre real or scatter" can be answered instead of guessed.
    /// </summary>
    public double? MedianErrorStandardErrorM { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<double> PerPointMedianErrorM { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<double> PerPointRoomHitRate { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<double> PerPointFloorHitRate { get; set; } = new();
}
