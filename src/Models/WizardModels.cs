using System.Text.Json.Serialization;

namespace ESPresense.Models;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public class ValidationIssue
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ValidationSeverity Severity { get; set; }

    /// <summary>Machine-readable issue category, e.g. "bounds_swapped", "room_overlap", "placement_mismatch".</summary>
    public string Category { get; set; } = "";

    public string Message { get; set; } = "";

    public string? FloorId { get; set; }
    public string? RoomId { get; set; }
    public string? NodeId { get; set; }
}

public class WizardValidationResult
{
    public List<ValidationIssue> Issues { get; set; } = new();
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);
}

public class HealthGateNode
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool Online { get; set; }
    public string? Version { get; set; }
    /// <summary>Seconds since last telemetry payload arrived, null if never seen.</summary>
    public double? TelemetryAgeSecs { get; set; }
    /// <summary>Seconds the node has been offline (null while online).</summary>
    public double? OfflineSecs { get; set; }
    public bool Stale { get; set; }
}

public class HealthGateResult
{
    public bool Passed { get; set; }
    public List<HealthGateNode> Nodes { get; set; } = new();
    public List<string> OfflineNodes { get; set; } = new();
    public List<string> StaleNodes { get; set; } = new();
    /// <summary>Distinct firmware versions seen across online nodes.</summary>
    public List<string> FirmwareVersions { get; set; } = new();
}

public class ExcludedPairSuggestion
{
    public string NodeA { get; set; } = "";
    public string NodeB { get; set; } = "";
    public string? NodeAName { get; set; }
    public string? NodeBName { get; set; }
    /// <summary>Exponentially-weighted average of |percent error| across recent samples (0.5 = 50%).</summary>
    public double AvgAbsPercentError { get; set; }
    /// <summary>Recent fraction of samples whose error exceeded the threshold (persistence measure, 1.0 = always bad).</summary>
    public double AboveThresholdFraction { get; set; }
    public int Samples { get; set; }
    /// <summary>How long this pair has been observed (wall-clock hours since first sample).</summary>
    public double ObservedHours { get; set; }
    /// <summary>Config-format pair id, "node_a:node_b".</summary>
    public string PairId => $"{NodeA}:{NodeB}";
}

/// <summary>
/// Measurement-level diagnostics, as opposed to <see cref="WizardValidationResult"/> which only
/// checks geometry. Everything here answers "do the numbers coming off the radio agree with the
/// map", which is where accuracy problems actually live.
/// </summary>
public class WizardDiagnosticsResult
{
    public List<ValidationIssue> Issues { get; set; } = new();

    /// <summary>Fit quality split by range - a single figure over all pairs hides the structure that explains most errors.</summary>
    public FitQuality Near { get; set; } = new();
    public FitQuality Far { get; set; } = new();

    /// <summary>Range that separates Near from Far, in metres.</summary>
    public double NearFarSplitM { get; set; }

    /// <summary>Per-pair signal plausibility, worst first.</summary>
    public List<SignalOutlier> SignalOutliers { get; set; } = new();

    /// <summary>Calibration parameters sitting on a configured limit - the optimizer wanted to go further.</summary>
    public List<ClampedParameter> ClampedParameters { get; set; } = new();

    /// <summary>
    /// Walk-Punkte ohne aufgezeichnete Pegel - die Nachhol-Liste. Strukturiert statt als Fliesstext,
    /// damit die Oberflaeche daraus eine abarbeitbare Tabelle machen und jeden Punkt auf der Karte
    /// zeigen kann; als Satz waren es 700 Zeichen, aus denen man sich die IDs klauben musste.
    /// </summary>
    public List<StaleWalkPoint> StaleWalkPoints { get; set; } = new();

    /// <summary>Device ids that belong to the same hardware address.</summary>
    public List<SplitIdentityInfo> SplitIdentities { get; set; } = new();

    /// <summary>Device ids whose address rotates, so the address-based split check cannot cover them.</summary>
    public List<string> RotatingAddressIds { get; set; } = new();

    /// <summary>Nodes relocated recently - their history against the old geometry is gone.</summary>
    public List<ESPresense.Services.NodeMove> NodeMoves { get; set; } = new();

    /// <summary>Per-room distance to the nearest node - the strongest predictor of accuracy found
    /// on this installation, and the one thing a user can act on directly.</summary>
    public List<RoomCoverage> RoomCoverage { get; set; } = new();
}

public class FitQuality
{
    public int Pairs { get; set; }
    /// <summary>Root mean square of (measured - map) distance, in metres.</summary>
    public double? RmseM { get; set; }
    /// <summary>Median signed distance error - reveals a systematic bias that RMSE hides.</summary>
    public double? MedianBiasM { get; set; }
    /// <summary>Median |measured - required| RSSI, in dB. Range-independent, unlike the metre figures.</summary>
    public double? MedianAbsRssiErrorDb { get; set; }
}

public class SignalOutlier
{
    public string RxId { get; set; } = "";
    public string? RxName { get; set; }
    public string TxId { get; set; } = "";
    public string? TxName { get; set; }
    public double MapDistanceM { get; set; }
    public double MeasuredDistanceM { get; set; }
    public double MeasuredRssi { get; set; }
    /// <summary>RSSI the path-loss model needs to produce the map distance with this node's absorption.</summary>
    public double RequiredRssi { get; set; }
    /// <summary>Measured minus required. Positive means "heard far louder than physically possible".</summary>
    public double DeltaDb { get; set; }
    public double Absorption { get; set; }
    /// <summary>
    /// How many snapshots the smoothed values above are built from. Deliberately NOT part of the
    /// message text: it increments on every request, so putting it there made each finding read as a
    /// new one - the exact churn the smoothing was added to remove.
    /// </summary>
    public int Observations { get; set; }
    /// <summary>
    /// True while this pair counts as a contradiction. Latches on at the contradiction threshold and
    /// off only at the lower release threshold, so a pair hovering at the boundary stops appearing
    /// and disappearing between polls.
    /// </summary>
    public bool Reported { get; set; }
    /// <summary>How long this pair has been under observation - separates "always was" from "just started".</summary>
    public double? ObservedHours { get; set; }
}

public class ClampedParameter
{
    public string NodeId { get; set; } = "";
    public string? NodeName { get; set; }
    public string Parameter { get; set; } = "";
    public double Value { get; set; }
    public double Limit { get; set; }
    public string Bound { get; set; } = "";
}

public class SplitIdentityInfo
{
    public string Mac { get; set; } = "";
    public List<SplitIdentityIdInfo> Ids { get; set; } = new();
}

public class SplitIdentityIdInfo
{
    public string Id { get; set; } = "";
    public string[] Nodes { get; set; } = Array.Empty<string>();
}

public class RoomCoverage
{
    public string FloorId { get; set; } = "";
    public string? RoomId { get; set; }
    public string? RoomName { get; set; }
    public int SampledPoints { get; set; }
    public double MedianNearestNodeM { get; set; }
    public double WorstNearestNodeM { get; set; }
    /// <summary>Share of the room within the distance that measured good accuracy here.</summary>
    public double WellCoveredFraction { get; set; }
}

/// <summary>Ein Walk-Punkt, der keine Pegel traegt und deshalb keine Kalibrierung bewerten kann.</summary>
public class StaleWalkPoint
{
    public string Id { get; set; } = "";
    public string? FloorId { get; set; }
    public string? FloorName { get; set; }
    public string? RoomName { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Ticks { get; set; }
    public DateTime RecordedAt { get; set; }
}
