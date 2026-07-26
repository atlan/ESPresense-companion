using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Services;

/// <summary>
/// Measurement-level diagnostics. <see cref="WizardService"/> validates the map (bounds, polygons,
/// node placement); this validates the radio data against that map.
///
/// The checks exist because a real accuracy investigation on 2026-07-26 needed raw MQTT, the
/// calibration matrix and hand arithmetic to find three problems the UI could have named outright:
/// a device split across two ids, a calibration parameter pinned to its configured limit, and nodes
/// reporting signal levels that no path-loss model can produce at their mapped distance. None of
/// those are "the fit is a bit off" - they are contradictions, and averaging them into an error
/// figure hides exactly the information needed to fix them.
/// </summary>
public class WizardDiagnostics(
    State state,
    NodeSettingsStore nodeSettings,
    ConfigLoader configLoader,
    DeviceIdentityTracker identityTracker,
    NodeMoveTracker moveTracker,
    WalkTestService walkTest)
{
    /// <summary>Pairs closer than this count as "near". Chosen because measured error stays within a
    /// couple of dB below it and diverges sharply above - see the field data in the roadmap.</summary>
    private const double NearFarSplitM = 4.0;

    /// <summary>Above this the measurement contradicts the map rather than merely disagreeing with it.</summary>
    private const double SignalContradictionDb = 15.0;

    /// <summary>
    /// Once reported, a pair keeps being reported until it falls below this. Without the gap, a pair
    /// sitting at 14-16 dB enters and leaves the list on alternate polls: measured 2026-07-26, 8 of
    /// 39 findings flipped across three requests 12 s apart. A list that reshuffles while it is being
    /// read cannot be worked through, which is the whole point of it.
    /// </summary>
    private const double SignalReleaseDb = 12.0;

    /// <summary>
    /// Smoothing on the per-pair gap. The snapshot is instantaneous and RSSI moves several dB between
    /// polls, so the raw value both flickers across the threshold AND changes the printed number -
    /// which made every signal finding read as a new entry even when it was the same one.
    /// </summary>
    /// Measured after the fact: at 0.3 the smoothed level still crossed a whole-dB boundary between
    /// most polls, so the printed number kept moving even though the finding did not. 0.1 is roughly
    /// a ten-sample window - slow enough that the text settles, fast enough that a node genuinely
    /// changing behaviour still surfaces within a couple of minutes of polling.
    private const double SignalEwmaAlpha = 0.1;

    /// <summary>Nothing is reported before a pair has been seen this often - one stray packet is not a finding.</summary>
    private const int MinSignalObservations = 3;

    /// <summary>Worth mentioning, not yet a contradiction.</summary>
    private const double SignalSuspiciousDb = 8.0;

    /// <summary>How close to a limit still counts as sitting on it (parameters are floating point).</summary>
    private const double ClampEpsilon = 0.01;

    /// <summary>Below this a pair is too noisy to judge - avoids flagging a single stray packet.</summary>
    private const double MinMapDistanceM = 0.5;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PairSignal> _pairSignals = new();

    /// <summary>Running view of one directed pair, so findings survive a single noisy sample.</summary>
    private sealed class PairSignal
    {
        public double DeltaDb;
        public double Rssi;
        public double MeasuredDistanceM;
        public double MapDistanceM;
        public double Absorption;
        public int Observations;
        public bool Reported;
    }

    public WizardDiagnosticsResult Analyze()
    {
        var result = new WizardDiagnosticsResult { NearFarSplitM = NearFarSplitM };
        var config = configLoader.Config;

        CheckSplitIdentities(result);
        CheckNodeMoves(result);
        CheckNodeCoverage(result);
        CheckClampedParameters(config, result);
        AnalyzeSignals(config, result);

        return result;
    }

    /// <summary>
    /// One hardware address reporting under several device ids means its measurements are split
    /// across separate solutions. Silent by nature: both devices look healthy on their own.
    /// </summary>
    private void CheckSplitIdentities(WizardDiagnosticsResult result)
    {
        foreach (var split in identityTracker.GetSplitIdentities())
        {
            result.SplitIdentities.Add(new SplitIdentityInfo
            {
                Mac = split.Mac,
                Ids = split.Ids.Select(i => new SplitIdentityIdInfo { Id = i.Id, Nodes = i.Nodes }).ToList()
            });

            var idList = string.Join("', '", split.Ids.Select(i => $"{i.Id} ({i.Nodes.Length} nodes)"));
            var main = split.Ids.First();
            var others = split.Ids.Skip(1).ToList();
            var lostNodes = others.Sum(o => o.Nodes.Length);
            var totalNodes = split.Ids.Sum(i => i.Nodes.Length);

            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Category = "identity",
                Message = $"Address {split.Mac} is reporting under {split.Ids.Count} device ids: '{idList}'. " +
                          $"They are the same hardware, so {lostNodes} of {totalNodes} node measurements never reach " +
                          $"'{main.Id}'. Publish a retained alias for the other ids, e.g. " +
                          $"espresense/settings/{others[0].Id}/config with {{\"id\":\"{main.Id}\"}} - " +
                          "aliases are keyed on the id a node derived, and nodes that resolved a different " +
                          "identifier look it up under a key that does not exist."
            });
        }

        foreach (var id in identityTracker.GetRotatingIds())
            result.RotatingAddressIds.Add(id);
    }

    /// <summary>
    /// Surfaces relocations and what they cost. Both consequences are already handled correctly
    /// elsewhere - walk measures get skipped, pair statistics get dropped - but silently, so a node
    /// quietly stops contributing history and nothing says why.
    /// </summary>
    private void CheckNodeMoves(WizardDiagnosticsResult result)
    {
        foreach (var move in moveTracker.GetMoves(MoveReportWindow))
        {
            // Count what this specific move invalidated, so the message is concrete rather than a warning.
            var affectedPoints = 0;
            var affectedMeasures = 0;
            foreach (var point in walkTest.GetPoints())
            {
                var lost = point.Nodes.Count(a =>
                    string.Equals(a.NodeId, move.NodeId, StringComparison.OrdinalIgnoreCase) &&
                    state.Nodes.TryGetValue(a.NodeId, out var n) && n.HasLocation &&
                    n.Location.DistanceTo(a.NodeLocationAtRecord) > NodeMoveTracker.MoveThresholdM);
                if (lost <= 0) continue;
                affectedPoints++;
                affectedMeasures += lost;
            }

            result.NodeMoves.Add(move);

            var reseeded = move.AbsorptionReseededTo is { } a
                ? $"Its absorption was re-seeded to the fleet median ({a:0.00}) so the next fit starts neutral; "
                : "";
            var walk = affectedMeasures > 0
                ? $"{affectedMeasures} walk measures across {affectedPoints} points are no longer counted, "
                : "";

            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "moved",
                NodeId = move.NodeId,
                Message = $"Node '{move.NodeName ?? move.NodeId}' moved {move.DistanceM:0.00} m on " +
                          $"{move.At:yyyy-MM-dd HH:mm} UTC. {walk}and its pair-error history was reset - " +
                          $"measurements taken against the old geometry cannot be reused. {reseeded}" +
                          "Expect its calibration to wander for a few runs until it re-converges."
            });
        }
    }

    /// <summary>
    /// Answers the question a newcomer actually has - "where do I put a node?" - instead of only
    /// reporting how large the error is.
    ///
    /// Measured on this installation (21 walk points, 2026-07-26): where a node stood within
    /// <see cref="GoodCoverageM"/>, the median position error averaged 1.11 m; beyond that, 2.47 m.
    /// The correlation with the distance to the THIRD-nearest node was r = -0.01, so this is not
    /// about node density or overall coverage - a single node close enough is what matters. It also
    /// explained an apparent per-floor difference outright: the upper floor scored 2.73 m against
    /// the ground floor's 0.78 m purely because its measured spots sat 2.3-4.0 m from the nearest
    /// node while the ground floor's sat under 1.2 m.
    ///
    /// The room polygon is sampled on a grid rather than reduced to its centroid, because a node in
    /// one corner of a long room leaves the far end just as uncovered as no node at all.
    /// </summary>
    private void CheckNodeCoverage(WizardDiagnosticsResult result)
    {
        foreach (var floor in state.Floors.Values)
        {
            var nodes = state.Nodes.Values
                .Where(n => n.HasLocation && (n.Floors?.Any(f => f.Id == floor.Id) ?? false))
                .Select(n => n.Location)
                .ToList();
            if (nodes.Count == 0 || floor.Rooms.IsEmpty) continue;

            // Height at which a tracked device is assumed to sit. Taken from the walk points on this
            // floor when there are any - measured beats guessed - otherwise the middle of the floor.
            var walkZ = walkTest.GetPoints()
                .Where(p => string.Equals(p.FloorId, floor.Id, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Z).OrderBy(z => z).ToList();
            var deviceZ = walkZ.Count > 0
                ? walkZ[walkZ.Count / 2]
                : (floor.Bounds is { Length: >= 2 } b ? (b[0].Z + b[1].Z) / 2 : 0);

            foreach (var room in floor.Rooms.Values)
            {
                if (room.Polygon == null) continue;
                var pts = room.Polygon.Vertices.ToList();
                if (pts.Count < 3) continue;

                var minX = pts.Min(p => p.X); var maxX = pts.Max(p => p.X);
                var minY = pts.Min(p => p.Y); var maxY = pts.Max(p => p.Y);

                var distances = new List<double>();
                for (var x = minX; x <= maxX; x += SampleStepM)
                for (var y = minY; y <= maxY; y += SampleStepM)
                {
                    var p2 = new Point2D(x, y);
                    if (!room.Polygon.EnclosesPoint(p2)) continue;
                    var p3 = new Point3D(x, y, deviceZ);
                    distances.Add(nodes.Min(n => n.DistanceTo(p3)));
                }
                if (distances.Count == 0) continue;

                distances.Sort();
                var coverage = new RoomCoverage
                {
                    FloorId = floor.Id ?? "",
                    RoomId = room.Id,
                    RoomName = room.Name,
                    SampledPoints = distances.Count,
                    MedianNearestNodeM = Math.Round(distances[distances.Count / 2], 2),
                    WorstNearestNodeM = Math.Round(distances[^1], 2),
                    WellCoveredFraction = Math.Round((double)distances.Count(d => d <= GoodCoverageM) / distances.Count, 2)
                };
                result.RoomCoverage.Add(coverage);

                if (coverage.WellCoveredFraction >= 0.5) continue;

                var expected = coverage.MedianNearestNodeM <= GoodCoverageM ? 1.1 : 2.5;
                result.Issues.Add(new ValidationIssue
                {
                    Severity = coverage.MedianNearestNodeM > PoorCoverageM ? ValidationSeverity.Warning : ValidationSeverity.Info,
                    Category = "coverage",
                    FloorId = floor.Id,
                    RoomId = room.Id,
                    Message = $"'{room.Name ?? room.Id}': the nearest node is {coverage.MedianNearestNodeM:0.0} m away " +
                              $"across the middle of the room, up to {coverage.WorstNearestNodeM:0.0} m at the far end, " +
                              $"and only {coverage.WellCoveredFraction:P0} of it lies within {GoodCoverageM:0.0} m of one. " +
                              $"Expect around {expected:0.0} m accuracy here - measured on this installation, spots with " +
                              $"a node inside {GoodCoverageM:0.0} m averaged 1.1 m error and spots beyond it 2.5 m. " +
                              "One additional node in this room helps more than any calibration change."
                });
            }
        }

        result.RoomCoverage = result.RoomCoverage.OrderByDescending(r => r.MedianNearestNodeM).ToList();
    }

    /// <summary>Grid spacing when sampling a room - fine enough to catch a long room with one node at one end.</summary>
    private const double SampleStepM = 0.5;

    /// <summary>Within this a spot measured 1.1 m median error on this installation; beyond it, 2.5 m.</summary>
    private const double GoodCoverageM = 1.5;

    /// <summary>Beyond this the room is reported as a warning rather than a note.</summary>
    private const double PoorCoverageM = 3.0;

    /// <summary>How far back a relocation is still worth reporting.</summary>
    private static readonly TimeSpan MoveReportWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// A parameter resting exactly on its limit means the optimizer wanted to go further and was not
    /// allowed to. The resulting fit is not "the best possible", it is "the best inside a box that
    /// excludes the answer" - and nothing in the UI says so today.
    /// </summary>
    private void CheckClampedParameters(Config? config, WizardDiagnosticsResult result)
    {
        var opt = config?.Optimization;
        if (opt == null) return;

        foreach (var node in state.Nodes.Values)
        {
            var cal = nodeSettings.Get(node.Id)?.Calibration;
            if (cal == null) continue;

            Check(node.Id, node.Name, "absorption", cal.Absorption, opt.AbsorptionMin, opt.AbsorptionMax);
            Check(node.Id, node.Name, "rx_adj_rssi", cal.RxAdjRssi, opt.RxAdjRssiMin, opt.RxAdjRssiMax);
            Check(node.Id, node.Name, "tx_ref_rssi", cal.TxRefRssi, opt.TxRefRssiMin, opt.TxRefRssiMax);
        }

        void Check(string nodeId, string? nodeName, string name, double? value, double min, double max)
        {
            if (value is not { } v) return;

            string? bound = null;
            double limit = 0;
            if (Math.Abs(v - min) <= ClampEpsilon) { bound = "min"; limit = min; }
            else if (Math.Abs(v - max) <= ClampEpsilon) { bound = "max"; limit = max; }
            if (bound == null) return;

            result.ClampedParameters.Add(new ClampedParameter
            {
                NodeId = nodeId, NodeName = nodeName, Parameter = name, Value = v, Limit = limit, Bound = bound
            });

            var key = name == "absorption" ? "limits.absorption" : $"limits.{name}";
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "clamped",
                NodeId = nodeId,
                Message = $"Node '{nodeName ?? nodeId}': {name} sits on its configured {bound} of {limit:0.##}. " +
                          $"The optimizer wanted to go past it, so this node's calibration is capped rather than " +
                          $"fitted - widen {key}_{bound} and re-run, or exclude the node if it is genuinely atypical."
            });
        }
    }

    /// <summary>
    /// Compares each pair's measured level with the level the path-loss model would need to produce
    /// the mapped distance, and reports fit quality separately for near and far pairs.
    ///
    /// Working in dB rather than metres is deliberate: the same few dB of noise is a few centimetres
    /// up close and several metres far away, so a metre-based figure is dominated by the pairs the
    /// model can least represent, and says little about the ones it can.
    /// </summary>
    private void AnalyzeSignals(Config? config, WizardDiagnosticsResult result)
    {
        var snapshot = state.TakeOptimizationSnapshot();
        if (snapshot.Measures.Count == 0) return;

        var opt = config?.Optimization;
        var fallbackAbsorption = opt == null ? 3.0 : opt.AbsorptionMin + (opt.AbsorptionMax - opt.AbsorptionMin) / 2.0;

        var near = new List<Sample>();
        var far = new List<Sample>();

        foreach (var m in snapshot.Measures)
        {
            var mapDistance = m.Rx.Location.DistanceTo(m.Tx.Location);
            if (mapDistance < MinMapDistanceM || m.Distance <= 0) continue;

            var absorption = nodeSettings.Get(m.Rx.Id)?.Calibration?.Absorption ?? fallbackAbsorption;
            if (absorption <= 0) continue;

            // Level the model needs at the mapped distance; the measured level minus this is the
            // part the model cannot explain.
            var requiredRssi = m.RefRssi - 10.0 * absorption * Math.Log10(mapDistance);
            var deltaDb = m.Rssi - requiredRssi;

            var key = $"{m.Rx.Id}\u0000{m.Tx.Id}";
            var signal = _pairSignals.GetOrAdd(key, _ => new PairSignal());
            lock (signal)
            {
                if (signal.Observations == 0)
                {
                    signal.DeltaDb = deltaDb;
                    signal.Rssi = m.Rssi;
                    signal.MeasuredDistanceM = m.Distance;
                }
                else
                {
                    signal.DeltaDb = SignalEwmaAlpha * deltaDb + (1 - SignalEwmaAlpha) * signal.DeltaDb;
                    signal.Rssi = SignalEwmaAlpha * m.Rssi + (1 - SignalEwmaAlpha) * signal.Rssi;
                    signal.MeasuredDistanceM = SignalEwmaAlpha * m.Distance + (1 - SignalEwmaAlpha) * signal.MeasuredDistanceM;
                }
                // Geometry and calibration are not measurements - no reason to smooth them.
                signal.MapDistanceM = mapDistance;
                signal.Absorption = absorption;
                signal.Observations++;

                // Summaries built from the smoothed pair values rather than the raw sample: it makes
                // the near/far figures stop wandering, and it weights every pair once instead of
                // letting a chatty pair count more than a quiet one.
                var sample = new Sample(signal.DeltaDb, signal.MeasuredDistanceM - mapDistance);
                (mapDistance <= NearFarSplitM ? near : far).Add(sample);

                if (signal.Observations < MinSignalObservations) continue;

                var magnitude = Math.Abs(signal.DeltaDb);
                signal.Reported = signal.Reported ? magnitude >= SignalReleaseDb : magnitude >= SignalContradictionDb;

                if (magnitude < SignalSuspiciousDb) continue;

                result.SignalOutliers.Add(new SignalOutlier
                {
                    RxId = m.Rx.Id, RxName = m.Rx.Name,
                    TxId = m.Tx.Id, TxName = m.Tx.Name,
                    MapDistanceM = Math.Round(mapDistance, 2),
                    MeasuredDistanceM = Math.Round(signal.MeasuredDistanceM, 2),
                    MeasuredRssi = Math.Round(signal.Rssi, 1),
                    RequiredRssi = Math.Round(requiredRssi, 1),
                    DeltaDb = Math.Round(signal.DeltaDb, 1),
                    Absorption = Math.Round(absorption, 2),
                    Observations = signal.Observations,
                    Reported = signal.Reported
                });
            }
        }

        result.Near = Summarize(near);
        result.Far = Summarize(far);
        // Tie-broken on the pair id so the cut at 20 does not reorder when two pairs sit at the same
        // rounded magnitude - otherwise the trimming itself becomes a source of churn.
        result.SignalOutliers = result.SignalOutliers
            .OrderByDescending(o => Math.Abs(o.DeltaDb))
            .ThenBy(o => o.RxId, StringComparer.Ordinal)
            .ThenBy(o => o.TxId, StringComparer.Ordinal)
            .Take(20)
            .ToList();

        foreach (var contradiction in result.SignalOutliers.Where(s => s.Reported))
        {
            var sign = contradiction.DeltaDb >= 0 ? "+" : "";
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "signal",
                NodeId = contradiction.RxId,
                Message = $"'{contradiction.RxName ?? contradiction.RxId}' hears " +
                          $"'{contradiction.TxName ?? contradiction.TxId}' at {contradiction.MeasuredRssi:0} dBm " +
                          $"from {contradiction.MapDistanceM:0.0} m away, but the model needs " +
                          $"{contradiction.RequiredRssi:0} dBm there ({sign}{contradiction.DeltaDb:0} dB off, " +
                          $"absorption {contradiction.Absorption:0.00}). " +
                          "No path-loss setting explains a gap this " +
                          "large - treat it as a contradiction (check the mapped position, the antenna, or exclude " +
                          "the pair) rather than something calibration can absorb."
            });
        }

        if (result.Near.Pairs >= 3 && result.Far.Pairs >= 3 &&
            result.Near.MedianAbsRssiErrorDb is { } nearErr && result.Far.MedianAbsRssiErrorDb is { } farErr &&
            farErr > nearErr * 2 && farErr - nearErr >= 5)
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "range",
                Message = $"The fit holds up close and falls apart with range: median error {nearErr:0.0} dB below " +
                          $"{NearFarSplitM:0} m versus {farErr:0.0} dB above it. That is the signature of an " +
                          "absorption fitted to near pairs and extrapolated too steeply - distant nodes then report " +
                          "far too short a distance and pull the position towards themselves."
            });
    }

    private static FitQuality Summarize(List<Sample> samples)
    {
        if (samples.Count == 0) return new FitQuality();
        var distErrors = samples.Select(s => s.DistanceErrorM).OrderBy(v => v).ToList();
        return new FitQuality
        {
            Pairs = samples.Count,
            RmseM = Math.Round(Math.Sqrt(samples.Average(s => s.DistanceErrorM * s.DistanceErrorM)), 2),
            MedianBiasM = Math.Round(Median(distErrors), 2),
            MedianAbsRssiErrorDb = Math.Round(Median(samples.Select(s => Math.Abs(s.RssiErrorDb)).OrderBy(v => v).ToList()), 1)
        };
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private readonly record struct Sample(double RssiErrorDb, double DistanceErrorM);
}
