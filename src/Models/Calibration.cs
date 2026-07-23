namespace ESPresense.Models;

public class Calibration
{
    public double? RMSE { get; set; }
    public double? R { get; set; }
    public Dictionary<string, Dictionary<string, Dictionary<string, double>>> Matrix { get; } = new();
    public OptimizerState OptimizerState { get; set; } = new();
    public HashSet<string> Anchored { get; } = new();
    /// <summary>Matrix row keys that are recorded walk-test points (ground truth at record time), not live nodes.</summary>
    public HashSet<string> WalkPoints { get; } = new();
}
