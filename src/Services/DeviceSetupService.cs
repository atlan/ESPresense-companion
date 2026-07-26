using ESPresense.Models;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Guided setup for a tracked device: find it, measure its 1 m reference level, and write the
/// alias under every id the fleet reports it as.
///
/// Why the reference measurement matters more than it looks: <c>rssi@1m</c> is a property of the
/// DEVICE, and the node-to-node calibration cannot supply it - the nodes calibrate each other, a
/// new tag is a stranger to all of them. Get it wrong and every distance for that device is
/// scaled by 10^(error / 10*absorption).
///
/// ★ It also cannot be inferred from the live data, which is why this measurement exists rather
/// than an estimator. Deriving it from device-to-node readings needs an assumed absorption, and
/// the estimate moves 17 dB across the plausible range of that assumption. At one metre the
/// distance term vanishes (log10(1) = 0), so the reading carries no such assumption.
///
/// ★★ Two lessons from the first field use (2026-07-26), both baked in below:
///
/// A single run is not a measurement. Repeating the identical 1 m placement produced -81.4 and
/// then -77.0 dBm - <see cref="RepeatabilityDb"/> of scatter from orientation, hand position and
/// the exact distance. A value is therefore only reported as trusted once
/// <see cref="MinRuns"/> runs agree; one run yields a provisional figure and says so.
///
/// Every audible node is recorded, not just the one being held next to. Two separate single-node
/// runs read 12 dB apart and nothing flagged it, because the cross-node check only ever looked
/// within one run. With all nodes captured, a node that hears far louder than its distance allows
/// shows up in the same run that produced the reference - and that turned out to be the more
/// important finding of the two.
/// </summary>
public class DeviceSetupService(
    State state,
    DeviceCaptureService capture,
    DeviceSettingsStore deviceSettings,
    DeviceIdentityTracker identityTracker)
{
    /// <summary>Below this many samples from a node its median is too shaky to trust.</summary>
    private const int MinSamplesPerNode = 8;

    /// <summary>Runs that must agree before the reference is reported as trusted.</summary>
    private const int MinRuns = 2;

    /// <summary>Measured scatter between identical repeats - the noise floor a run has to beat.</summary>
    private const double RepeatabilityDb = 4.4;

    /// <summary>Spread across runs above which the placement, not the device, is being measured.</summary>
    private const double RunSpreadWarnDb = RepeatabilityDb * 1.5;

    private ReferenceRun? _run;

    /// <summary>Completed runs per device+reference-node, so repeats can be compared.</summary>
    private readonly Dictionary<string, List<double>> _runHistory = new(StringComparer.OrdinalIgnoreCase);

    public List<DeviceSetupCandidate> GetCandidates()
    {
        var list = new List<DeviceSetupCandidate>();
        foreach (var device in state.Devices.Values)
        {
            if (device.LastSeen == null) continue;
            var settings = deviceSettings.Get(device.Id);

            list.Add(new DeviceSetupCandidate
            {
                Id = device.Id,
                Name = device.Name ?? settings?.Name,
                ConfiguredRefRssi = settings?.RefRssi,
                HasAlias = !string.IsNullOrWhiteSpace(settings?.Id) && settings.Id != device.Id,
                NodeCount = device.Nodes.Count,
                LastSeen = device.LastSeen,
                RotatingAddress = identityTracker.HasRotatingAddress(device.Id),
                AlternateIds = AlternateIdsFor(device.Id)
            });
        }

        return list.OrderBy(c => c.ConfiguredRefRssi.HasValue).ThenByDescending(c => c.NodeCount).ToList();
    }

    private string[] AlternateIdsFor(string deviceId) =>
        identityTracker.GetSplitIdentities()
            .FirstOrDefault(s => s.Ids.Any(i => i.Id == deviceId))
            ?.Ids.Select(i => i.Id).Where(i => i != deviceId).ToArray() ?? Array.Empty<string>();

    /// <summary>
    /// Begins a reference run. <paramref name="referenceNodeId"/> is the node the device is being
    /// held next to at <paramref name="distanceM"/>; every other node that hears it is recorded as
    /// context, because their levels relative to their own distances say more about the
    /// installation than the reference figure alone.
    /// </summary>
    public ReferenceStatus StartReference(string deviceId, string referenceNodeId, double distanceM)
    {
        capture.Discard(deviceId);
        capture.Start(deviceId, AlternateIdsFor(deviceId));
        _run = new ReferenceRun(deviceId, referenceNodeId, distanceM, DateTime.UtcNow);
        Log.Information("Reference run started: {Device} at {Distance} m from {Node}", deviceId, distanceM, referenceNodeId);
        return Status();
    }

    public ReferenceStatus Status()
    {
        if (_run == null) return new ReferenceStatus { Running = false };
        var result = Evaluate(_run, final: false);
        result.Running = capture.GetStatus(_run.DeviceId)?.active ?? false;
        return result;
    }

    public ReferenceStatus FinishReference()
    {
        if (_run == null) return new ReferenceStatus { Running = false };
        capture.Stop(_run.DeviceId);
        var result = Evaluate(_run, final: true);
        result.Running = false;

        var reference = result.Nodes.FirstOrDefault(n => n.IsReference && n.Trusted);
        if (reference != null)
        {
            var key = $"{_run.DeviceId}|{_run.ReferenceNodeId}";
            if (!_runHistory.TryGetValue(key, out var runs)) _runHistory[key] = runs = new List<double>();
            runs.Add(reference.RefRssiAtOneM);
            ApplyRunHistory(result, runs);
        }

        _run = null;
        return result;
    }

    public void CancelReference()
    {
        if (_run == null) return;
        capture.Discard(_run.DeviceId);
        _run = null;
    }

    /// <summary>Forgets previous runs for a device, e.g. after the tag was physically changed.</summary>
    public void ResetRuns(string deviceId)
    {
        foreach (var key in _runHistory.Keys.Where(k => k.StartsWith(deviceId + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _runHistory.Remove(key);
    }

    private void ApplyRunHistory(ReferenceStatus result, List<double> runs)
    {
        var sorted = runs.OrderBy(v => v).ToList();
        result.Runs = runs.Count;
        result.RunSpreadDb = Math.Round(sorted[^1] - sorted[0], 1);
        result.EstimatedRefRssi = (int)Math.Round(Median(sorted));
        result.Trusted = runs.Count >= MinRuns && result.RunSpreadDb <= RunSpreadWarnDb;

        if (runs.Count < MinRuns)
            result.Warning = $"Provisional after one run. Repeating the identical placement scatters by about " +
                             $"{RepeatabilityDb:0.0} dB (orientation, hand position, exact distance), so a single " +
                             $"reading cannot separate the device from how it was held. Run it once more.";
        else if (result.RunSpreadDb > RunSpreadWarnDb)
            result.Warning = $"The {runs.Count} runs differ by {result.RunSpreadDb:0.0} dB, more than placement scatter " +
                             $"explains (~{RepeatabilityDb:0.0} dB). Something changed between them - keep the device in " +
                             "the same orientation and at the same spot, and repeat.";
    }

    private ReferenceStatus Evaluate(ReferenceRun run, bool final)
    {
        var status = new ReferenceStatus
        {
            DeviceId = run.DeviceId,
            ReferenceNodeId = run.ReferenceNodeId,
            DistanceM = run.DistanceM,
            StartedUtc = run.StartedUtc,
            Final = final
        };

        if (capture.Export(run.DeviceId, null) is not { } ex) return status;

        foreach (var group in ex.messages.GroupBy(m => m.node))
        {
            var samples = group.ToList();
            if (samples.Count == 0) continue;
            var isReference = string.Equals(group.Key, run.ReferenceNodeId, StringComparison.OrdinalIgnoreCase);

            // Sensitivity-normalised level: the node reports the raw rssi and its receive adjustment
            // separately, and adjustments span -5..+25 dB across a fleet. Without adding it back the
            // reading would describe the node as much as the device.
            var levels = samples.Select(s => s.rssi + (s.rxAdj ?? 0)).OrderBy(v => v).ToList();
            var median = Median(levels);

            // Only the reference node has a known distance; for the others the mapped distance is
            // used, which is exactly what makes an implausible one visible.
            double? mapDistance = null;
            if (!isReference && state.Nodes.TryGetValue(group.Key, out var node) && node.HasLocation)
                mapDistance = null;   // device position is unknown during setup - see ContextNote

            status.Nodes.Add(new ReferenceNodeResult
            {
                NodeId = group.Key,
                NodeName = state.Nodes.TryGetValue(group.Key, out var n) ? n.Name : null,
                IsReference = isReference,
                Samples = samples.Count,
                MedianLevelDbm = Math.Round(median, 1),
                RefRssiAtOneM = Math.Round(isReference
                    ? median + 10.0 * AssumedAbsorptionForProjection * Math.Log10(Math.Max(run.DistanceM, 0.1))
                    : median, 1),
                MapDistanceM = mapDistance,
                Trusted = samples.Count >= MinSamplesPerNode
            });
        }

        status.Nodes = status.Nodes.OrderByDescending(n => n.IsReference).ThenByDescending(n => n.MedianLevelDbm).ToList();

        var reference = status.Nodes.FirstOrDefault(n => n.IsReference);
        if (reference == null)
            status.Warning = $"Node '{run.ReferenceNodeId}' did not report the device at all during the run - " +
                             "check the id, or that the device is actually within range of it.";
        else if (!reference.Trusted)
            status.Warning = $"Only {reference.Samples} readings from '{reference.NodeName ?? reference.NodeId}' " +
                             $"({MinSamplesPerNode} needed). Let it run longer.";
        else if (!final)
            status.EstimatedRefRssi = (int)Math.Round(reference.RefRssiAtOneM);

        // A node that hears louder than the one the device is being held against is saying something
        // impossible about itself, and it is worth catching here rather than a week later.
        var louder = status.Nodes.Where(n => !n.IsReference && reference != null && n.Trusted &&
                                             n.MedianLevelDbm > reference.MedianLevelDbm).ToList();
        if (louder.Count > 0)
            status.ContextNote = $"{string.Join(", ", louder.Select(l => l.NodeName ?? l.NodeId))} " +
                                 $"{(louder.Count == 1 ? "hears" : "hear")} the device louder than " +
                                 $"'{reference!.NodeName ?? reference.NodeId}' does from {run.DistanceM:0.0} m. " +
                                 "Either that node is considerably more sensitive than its receive adjustment says, " +
                                 "or it is not where the map puts it.";

        return status;
    }

    /// <summary>
    /// Only used to project a reading taken at something other than 1 m. Deliberately a plain
    /// free-space value rather than the node's fitted absorption: the fitted value is what this
    /// measurement is meant to be independent of.
    /// </summary>
    private const double AssumedAbsorptionForProjection = 2.0;

    /// <summary>
    /// Writes the reference level and name to the device config - and to every other id the same
    /// hardware reports under, because an alias on one id leaves the others publishing the raw name
    /// and feeding a second, partial solution.
    /// </summary>
    public async Task<DeviceSetupApplyResult> ApplyAsync(string deviceId, int refRssi, string? name, string? alias)
    {
        var targetId = string.IsNullOrWhiteSpace(alias) ? deviceId : alias;
        var ids = new List<string> { deviceId };
        ids.AddRange(AlternateIdsFor(deviceId));

        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = deviceSettings.Get(id) ?? new DeviceSettings { OriginalId = id };
            existing.Id = targetId;
            existing.Name = name ?? existing.Name ?? targetId;
            existing.RefRssi = refRssi;
            await deviceSettings.Set(id, existing);
            Log.Information("Device setup: wrote rssi@1m={Ref} and alias '{Alias}' for id {Id}", refRssi, targetId, id);
        }

        return new DeviceSetupApplyResult
        {
            Alias = targetId,
            RefRssi = refRssi,
            WrittenIds = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private sealed record ReferenceRun(string DeviceId, string ReferenceNodeId, double DistanceM, DateTime StartedUtc);
}

public class DeviceSetupCandidate
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int? ConfiguredRefRssi { get; set; }
    public bool HasAlias { get; set; }
    public int NodeCount { get; set; }
    public DateTime? LastSeen { get; set; }
    /// <summary>Address rotates (phone/watch) - needs an IRK, an alias on the address will not stick.</summary>
    public bool RotatingAddress { get; set; }
    public string[] AlternateIds { get; set; } = Array.Empty<string>();
}

public class ReferenceStatus
{
    public bool Running { get; set; }
    public bool Final { get; set; }
    public string DeviceId { get; set; } = "";
    public string ReferenceNodeId { get; set; } = "";
    public double DistanceM { get; set; }
    public DateTime StartedUtc { get; set; }
    public List<ReferenceNodeResult> Nodes { get; set; } = new();

    public int? EstimatedRefRssi { get; set; }
    /// <summary>Completed runs for this device and reference node.</summary>
    public int Runs { get; set; }
    /// <summary>Spread across those runs - compare against the ~4.4 dB placement scatter.</summary>
    public double RunSpreadDb { get; set; }
    /// <summary>Enough agreeing runs to act on the figure.</summary>
    public bool Trusted { get; set; }

    public string? Warning { get; set; }
    /// <summary>Something the surrounding nodes revealed, independent of the reference itself.</summary>
    public string? ContextNote { get; set; }
}

public class ReferenceNodeResult
{
    public string NodeId { get; set; } = "";
    public string? NodeName { get; set; }
    /// <summary>The node the device was held next to at the stated distance.</summary>
    public bool IsReference { get; set; }
    public int Samples { get; set; }
    public double MedianLevelDbm { get; set; }
    public double RefRssiAtOneM { get; set; }
    public double? MapDistanceM { get; set; }
    public bool Trusted { get; set; }
}

public class DeviceSetupApplyResult
{
    public string Alias { get; set; } = "";
    public int RefRssi { get; set; }
    public List<string> WrittenIds { get; set; } = new();
}
