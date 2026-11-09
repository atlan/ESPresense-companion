namespace ESPresense.Models;

public class Calibration
{
    public double? RMSE { get; set; }
    public double? R { get; set; }
    public double? BestRMSE { get; set; }
    public double? BestR { get; set; }
    public Dictionary<string, Dictionary<string, CalibrationCell>> Matrix { get; } = new();
    public OptimizerState OptimizerState { get; set; } = new();
    public HashSet<string> Anchored { get; } = new();
    /// <summary>Matrix row keys that are recorded walk-test points (ground truth at record time), not live nodes.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string> WalkPoints { get; } = new();

    /// <summary>Only on the wire when there are any, so an installation without walk points gets the upstream payload.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("walkPoints")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? WalkPointsOrNull => WalkPoints.Count > 0 ? WalkPoints : null;
}
