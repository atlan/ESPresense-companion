using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// Stage 2 of the rebuild: the residual the per-node fit minimizes.
///
/// The original form squares metres and applies a one-sided 4th power whenever the model predicts a
/// shorter distance than the map. Both parts are tested here against synthetic data where the true
/// absorption is known by construction, because "the fit looks plausible" is exactly the standard
/// this rebuild exists to replace.
/// </summary>
public class PerNodeObjectiveTests
{
    private State _state = null!;
    private string _dir = null!;
    private ConfigLoader _configLoader = null!;

    private const double TrueAbsorption = 4.0;
    private const double TrueTxRef = -59.0;

    [SetUp]
    public async Task Setup()
    {
        _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.yaml"), @"mqtt:
  host: localhost
optimization:
  enabled: true
  optimizer: per_node_absorption
  limits:
    absorption_min: 1.5
    absorption_max: 6.5
    rx_adj_rssi_min: -5
    rx_adj_rssi_max: 25
    tx_ref_rssi_min: -80
    tx_ref_rssi_max: -40
");
        _configLoader = new ConfigLoader(_dir);
        await _configLoader.ConfigAsync();
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static OptNode Node(string id, double x, double y) =>
        new() { Id = id, Name = id, Location = new Point3D(x, y, 2.4), FloorIds = new[] { "ground" } };

    /// <summary>
    /// Every ordered pair among six nodes, with the level that the given absorption would actually
    /// produce at the mapped distance. No noise: a fit that cannot recover the truth from perfect
    /// data has nothing to offer imperfect data.
    ///
    /// Six and not four for a reason found the hard way: the model has three free parameters per
    /// node (rxAdj, absorption, txRef), so N nodes give N(N-1) equations against 3N unknowns. At
    /// four they are exactly equal and the system can satisfy ANY data perfectly, including a
    /// poisoned pair - both objectives then ran to the absorption ceiling and the comparison
    /// measured nothing. Six gives 30 equations against 18 unknowns, which is what makes an outlier
    /// something the fit has to trade off rather than absorb.
    /// </summary>
    private static OptimizationSnapshot CleanSnapshot()
    {
        var nodes = new[]
        {
            Node("a", 0, 0), Node("b", 5, 0), Node("c", 0, 5),
            Node("d", 5, 5), Node("e", 2.5, 8), Node("f", 8, 2.5)
        };
        var os = new OptimizationSnapshot();

        foreach (var rx in nodes)
        foreach (var tx in nodes)
        {
            if (rx.Id == tx.Id) continue;
            var d = rx.Location.DistanceTo(tx.Location);
            var rssi = TrueTxRef - 10.0 * TrueAbsorption * Math.Log10(d);
            os.Measures.Add(new Measure
            {
                Rx = rx, Tx = tx,
                Rssi = rssi, RssiRxAdj = 0, RssiVar = 1.0, RefRssi = TrueTxRef,
                Distance = d, DistVar = 0.1
            });
        }

        return os;
    }

    private Dictionary<string, NodeSettings> Existing(OptimizationSnapshot os, double? absorption = null)
    {
        var d = new Dictionary<string, NodeSettings>();
        foreach (var id in os.GetNodeIds())
        {
            var s = new NodeSettings(id);
            if (absorption is { } a) s.Calibration.Absorption = a;
            d[id] = s;
        }
        return d;
    }

    private static double MedianAbsorption(OptimizationResults r)
    {
        var values = r.Nodes.Values.Select(n => n.Absorption).Where(a => a.HasValue).Select(a => a!.Value).OrderBy(a => a).ToList();
        return values[values.Count / 2];
    }

    [Test]
    public void DbObjective_RecoversTheTrueAbsorptionFromCleanData()
    {
        var os = CleanSnapshot();
        var fit = new PerNodeAbsorptionRxTx(_state)
        {
            ObjectiveOverride = "db",
            AbsorptionPenaltyOverride = 0
        }.Optimize(os, Existing(os));

        Assert.That(MedianAbsorption(fit), Is.EqualTo(TrueAbsorption).Within(0.2),
            "levels were generated with absorption 4.0 - a dB residual over them must lead back to it");
    }

    [Test]
    public void DbObjective_SurvivesOneContradictoryPairBetterThanSquaredMetres()
    {
        // One pair heard 25 dB too weakly, as if a concrete wall sat between two nodes the map says
        // are in the same room. The live diagnostics report twenty such contradictions on the real
        // installation, so this is the normal case, not a contrived one.
        static void Poison(OptimizationSnapshot os)
        {
            var victim = os.Measures.First(m => m.Rx.Id == "a" && m.Tx.Id == "d");
            victim.Rssi -= 25;
            victim.Distance = Math.Pow(10, (victim.RefRssi - victim.Rssi) / (10.0 * TrueAbsorption));
        }

        var dbSnapshot = CleanSnapshot(); Poison(dbSnapshot);
        var distSnapshot = CleanSnapshot(); Poison(distSnapshot);

        var db = MedianAbsorption(new PerNodeAbsorptionRxTx(_state)
        { ObjectiveOverride = "db", AbsorptionPenaltyOverride = 0 }.Optimize(dbSnapshot, Existing(dbSnapshot)));

        var distance = MedianAbsorption(new PerNodeAbsorptionRxTx(_state)
        { ObjectiveOverride = "distance", AbsorptionPenaltyOverride = 0 }.Optimize(distSnapshot, Existing(distSnapshot)));

        Assert.That(Math.Abs(db - TrueAbsorption), Is.LessThan(Math.Abs(distance - TrueAbsorption)),
            $"dB+Huber landed at {db:0.00}, squared metres at {distance:0.00}, truth is {TrueAbsorption} - " +
            "the point of the robust residual is that one impossible pair pulls without dictating");
    }

    [Test]
    public void Regularization_PullsTowardsTheFleetMedianNotTheMidpointOfTheLimits()
    {
        // Limits 1.5..6.5 put the midpoint at 4.0. If the target still came from the limits, seeding
        // every node at 5.5 would change nothing about where the penalty pulls.
        var os = CleanSnapshot();
        var optimizer = new PerNodeAbsorptionRxTx(_state) { ObjectiveOverride = "db", AbsorptionPenaltyOverride = 1 };
        optimizer.Optimize(os, Existing(os, absorption: 5.5));

        Assert.That(optimizer.LastTargetAbsorption, Is.EqualTo(5.5).Within(0.01),
            "the fleet sits at 5.5, so that is what the regularization should shrink towards - " +
            "the midpoint of the configured limits describes the box, not the data");
    }

    [Test]
    public void Regularization_ExplicitTargetWinsOverTheFleet()
    {
        var os = CleanSnapshot();
        var optimizer = new PerNodeAbsorptionRxTx(_state)
        {
            ObjectiveOverride = "db",
            AbsorptionPenaltyOverride = 1,
            AbsorptionTargetOverride = 3.0
        };
        optimizer.Optimize(os, Existing(os, absorption: 5.5));

        Assert.That(optimizer.LastTargetAbsorption, Is.EqualTo(3.0).Within(0.01));
    }

    [Test]
    public void DistanceObjective_RemainsAvailableUnchanged()
    {
        // The old residual is still what a stock configuration runs. It has to keep working, or the
        // comparison the sweep performs is between a new thing and a broken thing.
        var os = CleanSnapshot();
        var fit = new PerNodeAbsorptionRxTx(_state)
        {
            ObjectiveOverride = "distance",
            AbsorptionPenaltyOverride = 0
        }.Optimize(os, Existing(os));

        Assert.That(MedianAbsorption(fit), Is.EqualTo(TrueAbsorption).Within(0.3));
    }
}
