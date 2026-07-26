using ESPresense.Models;

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
    DeviceIdentityTracker identityTracker)
{
    /// <summary>Pairs closer than this count as "near". Chosen because measured error stays within a
    /// couple of dB below it and diverges sharply above - see the field data in the roadmap.</summary>
    private const double NearFarSplitM = 4.0;

    /// <summary>Above this the measurement contradicts the map rather than merely disagreeing with it.</summary>
    private const double SignalContradictionDb = 15.0;

    /// <summary>Worth mentioning, not yet a contradiction.</summary>
    private const double SignalSuspiciousDb = 8.0;

    /// <summary>How close to a limit still counts as sitting on it (parameters are floating point).</summary>
    private const double ClampEpsilon = 0.01;

    /// <summary>Below this a pair is too noisy to judge - avoids flagging a single stray packet.</summary>
    private const double MinMapDistanceM = 0.5;

    public WizardDiagnosticsResult Analyze()
    {
        var result = new WizardDiagnosticsResult { NearFarSplitM = NearFarSplitM };
        var config = configLoader.Config;

        CheckSplitIdentities(result);
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

            var sample = new Sample(deltaDb, m.Distance - mapDistance);
            (mapDistance <= NearFarSplitM ? near : far).Add(sample);

            if (Math.Abs(deltaDb) >= SignalSuspiciousDb)
                result.SignalOutliers.Add(new SignalOutlier
                {
                    RxId = m.Rx.Id, RxName = m.Rx.Name,
                    TxId = m.Tx.Id, TxName = m.Tx.Name,
                    MapDistanceM = Math.Round(mapDistance, 2),
                    MeasuredDistanceM = Math.Round(m.Distance, 2),
                    MeasuredRssi = Math.Round(m.Rssi, 1),
                    RequiredRssi = Math.Round(requiredRssi, 1),
                    DeltaDb = Math.Round(deltaDb, 1),
                    Absorption = Math.Round(absorption, 2)
                });
        }

        result.Near = Summarize(near);
        result.Far = Summarize(far);
        result.SignalOutliers = result.SignalOutliers.OrderByDescending(o => Math.Abs(o.DeltaDb)).Take(20).ToList();

        foreach (var contradiction in result.SignalOutliers.Where(s => Math.Abs(s.DeltaDb) >= SignalContradictionDb))
        {
            var sign = contradiction.DeltaDb >= 0 ? "+" : "";
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "signal",
                NodeId = contradiction.RxId,
                Message = $"'{contradiction.RxName ?? contradiction.RxId}' hears " +
                          $"'{contradiction.TxName ?? contradiction.TxId}' at {contradiction.MeasuredRssi:0.0} dBm " +
                          $"from {contradiction.MapDistanceM:0.0} m away, but the model needs " +
                          $"{contradiction.RequiredRssi:0.0} dBm there ({sign}{contradiction.DeltaDb:0.0} dB off, " +
                          $"absorption {contradiction.Absorption:0.00}). No path-loss setting explains a gap this " +
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
