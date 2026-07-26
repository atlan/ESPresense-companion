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
/// scaled by 10^(error / 10*absorption): 6 dB off with absorption 3 is already a factor of 1.6,
/// and nothing in the UI reveals it.
///
/// ★ It also cannot be inferred from the live data, which is why this measurement exists rather
/// than an estimator. Deriving it from device-to-node readings needs an assumed absorption, and
/// the estimate moves 17 dB across the plausible range of that assumption - you can obtain almost
/// any answer you like. At one metre the distance term vanishes (log10(1) = 0), so the reading is
/// the reference, with no assumption in it at all. That is the whole point of walking over to a
/// node with the tag in hand.
/// </summary>
public class DeviceSetupService(
    State state,
    DeviceCaptureService capture,
    DeviceSettingsStore deviceSettings,
    DeviceIdentityTracker identityTracker)
{
    /// <summary>Below this many samples from a node its median is too shaky to trust.</summary>
    private const int MinSamplesPerNode = 8;

    /// <summary>Spread across participating nodes above which the result is reported as unreliable
    /// (in dB). Usually means the device was not actually at the stated distance from all of them.</summary>
    private const double NodeSpreadWarnDb = 8.0;

    private ReferenceRun? _run;

    /// <summary>Devices that have been heard but carry no configured reference level yet.</summary>
    public List<DeviceSetupCandidate> GetCandidates()
    {
        var list = new List<DeviceSetupCandidate>();
        foreach (var device in state.Devices.Values)
        {
            if (device.LastSeen == null) continue;
            var settings = deviceSettings.Get(device.Id);
            var configured = settings?.RefRssi;

            list.Add(new DeviceSetupCandidate
            {
                Id = device.Id,
                Name = device.Name ?? settings?.Name,
                ConfiguredRefRssi = configured,
                HasAlias = !string.IsNullOrWhiteSpace(settings?.Id) && settings.Id != device.Id,
                NodeCount = device.Nodes.Count,
                LastSeen = device.LastSeen,
                RotatingAddress = identityTracker.HasRotatingAddress(device.Id),
                // An id the fleet also reports this hardware as - setting the alias on only one of
                // them leaves the others feeding a separate, half-blind solution.
                AlternateIds = identityTracker.GetSplitIdentities()
                    .FirstOrDefault(s => s.Ids.Any(i => i.Id == device.Id))
                    ?.Ids.Select(i => i.Id).Where(i => i != device.Id).ToArray() ?? Array.Empty<string>()
            });
        }

        return list.OrderBy(c => c.ConfiguredRefRssi.HasValue).ThenByDescending(c => c.NodeCount).ToList();
    }

    /// <summary>Begins a reference measurement. The user holds the device at <paramref name="distanceM"/>
    /// from the listed nodes - one is enough, several make the result robust against a single node's
    /// odd receive calibration.</summary>
    public ReferenceStatus StartReference(string deviceId, string[] nodeIds, double distanceM)
    {
        var alternates = identityTracker.GetSplitIdentities()
            .FirstOrDefault(s => s.Ids.Any(i => i.Id == deviceId))
            ?.Ids.Select(i => i.Id).Where(i => i != deviceId).ToArray() ?? Array.Empty<string>();

        capture.Discard(deviceId);
        capture.Start(deviceId, alternates);
        _run = new ReferenceRun(deviceId, nodeIds, distanceM, DateTime.UtcNow);
        Log.Information("Reference measurement started for {Device} at {Distance} m from {Nodes}",
            deviceId, distanceM, string.Join(", ", nodeIds));
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
        return result;
    }

    public void CancelReference()
    {
        if (_run == null) return;
        capture.Discard(_run.DeviceId);
        _run = null;
    }

    private ReferenceStatus Evaluate(ReferenceRun run, bool final)
    {
        var export = capture.Export(run.DeviceId, null);
        var status = new ReferenceStatus
        {
            DeviceId = run.DeviceId,
            DistanceM = run.DistanceM,
            StartedUtc = run.StartedUtc,
            Final = final
        };

        if (export is not { } ex) return status;

        var wanted = new HashSet<string>(run.NodeIds, StringComparer.OrdinalIgnoreCase);
        foreach (var group in ex.messages.Where(m => wanted.Count == 0 || wanted.Contains(m.node)).GroupBy(m => m.node))
        {
            var samples = group.ToList();
            if (samples.Count == 0) continue;

            // Sensitivity-normalised level: the node reports the raw rssi and its receive adjustment
            // separately (see Measure.GetAdjustedRssi), and adjustments span -5..+25 dB across a
            // fleet. Without adding it back, the measurement would mostly describe the node.
            var levels = samples.Select(s => s.rssi + (s.rxAdj ?? 0)).OrderBy(v => v).ToList();
            var median = Median(levels);

            // Project to one metre. At the recommended 1 m this term is zero; it exists so a user
            // who cannot get that close (a node behind a cupboard) still gets a usable reading.
            var refAtOneM = median + 10.0 * AssumedAbsorptionForProjection * Math.Log10(Math.Max(run.DistanceM, 0.1));

            status.Nodes.Add(new ReferenceNodeResult
            {
                NodeId = group.Key,
                NodeName = state.Nodes.TryGetValue(group.Key, out var n) ? n.Name : null,
                Samples = samples.Count,
                MedianLevelDbm = Math.Round(median, 1),
                RefRssiAtOneM = Math.Round(refAtOneM, 1),
                Trusted = samples.Count >= MinSamplesPerNode
            });
        }

        var trusted = status.Nodes.Where(n => n.Trusted).ToList();
        if (trusted.Count == 0) return status;

        var estimates = trusted.Select(n => n.RefRssiAtOneM).OrderBy(v => v).ToList();
        status.EstimatedRefRssi = (int)Math.Round(Median(estimates));
        status.SpreadDb = Math.Round(estimates.Max() - estimates.Min(), 1);
        status.TrustedNodes = trusted.Count;

        if (trusted.Count > 1 && status.SpreadDb > NodeSpreadWarnDb)
            status.Warning = $"The participating nodes disagree by {status.SpreadDb:0.0} dB. That is more than " +
                             "receive calibration explains, so the device was probably not at the stated distance " +
                             "from all of them - repeat with a single node you can measure precisely.";

        return status;
    }

    /// <summary>
    /// Only used to project a reading taken at something other than 1 m. Deliberately a plain
    /// free-space-ish value rather than the node's fitted absorption: the fitted value is what this
    /// measurement is meant to be independent of, and at distances near 1 m the term is small anyway.
    /// </summary>
    private const double AssumedAbsorptionForProjection = 2.0;

    /// <summary>
    /// Writes the reference level and name to the device config - and to every other id the same
    /// hardware is reporting under, because an alias set on one id leaves the others publishing the
    /// raw name and feeding a second, partial solution.
    /// </summary>
    public async Task<DeviceSetupApplyResult> ApplyAsync(string deviceId, int refRssi, string? name, string? alias)
    {
        var targetId = string.IsNullOrWhiteSpace(alias) ? deviceId : alias;
        var ids = new List<string> { deviceId };
        ids.AddRange(identityTracker.GetSplitIdentities()
            .FirstOrDefault(s => s.Ids.Any(i => i.Id == deviceId))
            ?.Ids.Select(i => i.Id).Where(i => i != deviceId) ?? Array.Empty<string>());

        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = deviceSettings.Get(id) ?? new DeviceSettings { OriginalId = id };
            existing.Id = targetId;
            existing.Name = name ?? existing.Name ?? targetId;
            existing.RefRssi = refRssi;
            await deviceSettings.Set(id, existing);
            Log.Information("Device setup: wrote rssi@1m={Ref} and alias '{Alias}' for id {Id}", refRssi, targetId, id);
        }

        return new DeviceSetupApplyResult { Alias = targetId, RefRssi = refRssi, WrittenIds = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private sealed record ReferenceRun(string DeviceId, string[] NodeIds, double DistanceM, DateTime StartedUtc);
}

public class DeviceSetupCandidate
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int? ConfiguredRefRssi { get; set; }
    public bool HasAlias { get; set; }
    public int NodeCount { get; set; }
    public DateTime? LastSeen { get; set; }
    /// <summary>Address rotates (phone/watch) - such a device needs an IRK, an alias on the address will not stick.</summary>
    public bool RotatingAddress { get; set; }
    public string[] AlternateIds { get; set; } = Array.Empty<string>();
}

public class ReferenceStatus
{
    public bool Running { get; set; }
    public bool Final { get; set; }
    public string DeviceId { get; set; } = "";
    public double DistanceM { get; set; }
    public DateTime StartedUtc { get; set; }
    public List<ReferenceNodeResult> Nodes { get; set; } = new();
    public int? EstimatedRefRssi { get; set; }
    public int TrustedNodes { get; set; }
    public double SpreadDb { get; set; }
    public string? Warning { get; set; }
}

public class ReferenceNodeResult
{
    public string NodeId { get; set; } = "";
    public string? NodeName { get; set; }
    public int Samples { get; set; }
    public double MedianLevelDbm { get; set; }
    public double RefRssiAtOneM { get; set; }
    public bool Trusted { get; set; }
}

public class DeviceSetupApplyResult
{
    public string Alias { get; set; } = "";
    public int RefRssi { get; set; }
    public List<string> WrittenIds { get; set; } = new();
}
