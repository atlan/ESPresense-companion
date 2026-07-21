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
