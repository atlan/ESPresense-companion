using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

/// <summary>
/// One recorded tick, run through the REAL scenario competition - every enabled locator on every
/// floor, winner by confidence, which is what <see cref="MultiScenarioLocator"/> feeds its Kalman
/// filter from.
///
/// ★ Why this is shared rather than reimplemented per wizard panel. Every offline yardstick in this
/// project started life calling one estimator directly, and every one of them then reported a number
/// that its own subject did not deliver:
///
/// - <see cref="CalibrationBenchmark"/> replayed Nadaraya-Watson arithmetic and reported 99.8 % floor
///   accuracy for a spot the live system got right 46 % of the time.
/// - The locator tuner did the same, and additionally pre-filtered the audible nodes to the point's
///   TRUE floor - so it scored the estimators with the floor decision, the hardest part, already
///   given away.
///
/// Both flatter whichever estimator they happen to call, because the live answer is not one
/// estimator's output: it is the winner of a competition between all enabled locators across all
/// floors. A yardstick narrower than its subject is worse than none, because it is trusted. So there
/// is exactly one replay, here, and the panels differ only in what they vary and what they report.
///
/// The Kalman smoothing and motion-consistency weighting layered above are deliberately NOT
/// reproduced: they describe how a moving device is followed, while a walk point is a device standing
/// still, and reproducing them would only blur the comparison between candidates.
/// </summary>
public class ScenarioReplay(State state, ConfigLoader configLoader)
{
    /// <summary>Mirrors the live path: a locator needs three ranges before it says anything.</summary>
    public const int MinNodesPerTick = 3;

    /// <summary>Below this a walk point has too little to say and is not counted.</summary>
    public const int MinTicksPerPoint = 5;

    public sealed class Options
    {
        /// <summary>Locator names to let compete, e.g. nadaraya_watson / nelder_mead / mle / bfgs / nearest_node.</summary>
        public IReadOnlyList<string> Locators { get; init; } = Array.Empty<string>();

        public double ContrastWeight { get; init; }

        /// <summary>Null = use the configured value. Set only by the tuner, which scores candidates.</summary>
        public double? NadarayaWatsonBandwidth { get; init; }

        public string? NadarayaWatsonKernel { get; init; }

        /// <summary>Consistency filter override for the benchmark's "what would this setting do" runs.</summary>
        public bool? ConsistencyFilter { get; init; }

        public double? ConsistencyToleranceM { get; init; }

        public double? ConsistencyToleranceFraction { get; init; }
    }

    /// <summary>Locators currently enabled in the configuration - the combination the live system runs.</summary>
    public IReadOnlyList<string> ConfiguredLocators()
    {
        var l = configLoader.Config?.Locators;
        var names = new List<string>();
        if (l?.NadarayaWatson?.Enabled ?? false) names.Add("nadaraya_watson");
        if (l?.NelderMead?.Enabled ?? false) names.Add("nelder_mead");
        if (l?.Mle?.Enabled ?? false) names.Add("mle");
        if (l?.Bfgs?.Enabled ?? false) names.Add("bfgs");
        if (l?.NearestNode?.Enabled ?? false) names.Add("nearest_node");
        return names;
    }

    public double ConfiguredContrastWeight() => configLoader.Config?.Locators?.FloorContrastWeight ?? 0;

    /// <summary>
    /// Returns the winning scenario for one tick, or null when nothing reached a usable confidence.
    /// <paramref name="readings"/> is what the nodes heard: node plus its reported distance.
    /// </summary>
    public Scenario? BestScenario(IReadOnlyList<(Node node, double dist)> readings,
        IReadOnlyList<Floor> floors, Options options)
    {
        if (readings.Count < MinNodesPerTick) return null;

        var config = configLoader.Config;
        var device = new Device($"replay-{Guid.Empty}", null, TimeSpan.FromSeconds(30));
        foreach (var (node, dist) in readings)
            device.Nodes[node.Id] = new DeviceToNode(device, node)
            {
                Distance = dist, LastDistance = dist, DistVar = 0.1,
                Rssi = -70, RefRssi = -59, RssiVar = 1.0,
                LastHit = DateTime.UtcNow, Hits = 10
            };

        var scenarios = new List<Scenario>();
        foreach (var floor in floors)
        foreach (var name in options.Locators)
        {
            ILocate? locator = name switch
            {
                "nadaraya_watson" => new NadarayaWatsonMultilateralizer(device, floor, state, state.NodeTelemetry)
                {
                    BandwidthOverride = options.NadarayaWatsonBandwidth,
                    KernelOverride = options.NadarayaWatsonKernel,
                    ConsistencyFilterOverride = options.ConsistencyFilter,
                    ConsistencyToleranceMOverride = options.ConsistencyToleranceM,
                    ConsistencyToleranceFractionOverride = options.ConsistencyToleranceFraction
                },
                "nelder_mead" => new NelderMeadMultilateralizer(device, floor, state),
                "mle" => new MLEMultilateralizer(device, floor, state),
                "bfgs" => new BfgsMultilateralizer(device, floor, state),
                _ => null
            };
            if (locator != null) scenarios.Add(new Scenario(config, locator, floor.Name));
        }

        if (options.Locators.Contains("nearest_node"))
            scenarios.Add(new Scenario(config, new NearestNode(device, state), "NearestNode"));

        foreach (var scenario in scenarios) scenario.Locate();

        // The same cross-floor contrast the live locator applies, and for the same reason: without it
        // the locators that do not know about other floors decide the storey on their own.
        if (options.ContrastWeight > 0)
            foreach (var scenario in scenarios)
            {
                if (scenario.Floor is not { } floor || scenario.Confidence is not { } confidence) continue;
                var adjusted = confidence + FloorContrast.Adjustment(
                    scenario.Location, readings, r => r.node.Location, r => r.dist,
                    r => r.node.Floors?.Contains(floor) ?? false, options.ContrastWeight);
                scenario.Confidence = (int)Math.Round(Math.Clamp(adjusted, 0, 100));
            }

        return scenarios.Where(s => s.Confidence > 0).MaxBy(s => s.Confidence);
    }

    /// <summary>2D error against a known truth - Z is dominated by node mounting heights, not locator quality.</summary>
    public static double Error2D(Point3D est, Point3D truth) =>
        Math.Sqrt(Math.Pow(est.X - truth.X, 2) + Math.Pow(est.Y - truth.Y, 2));
}
