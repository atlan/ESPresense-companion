using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class PairErrorTrackerTests
{
    private State _state = null!;
    private string _configDir = null!;
    private ConfigLoader _configLoader = null!;
    private PairErrorTracker _tracker = null!;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        _tracker = new PairErrorTracker(_state);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, true);
    }

    private Floor MakeFloor(string id)
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor { Id = id, Name = id, Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 } } });
        return floor;
    }

    private Node MakeNode(string id, double x, double y, Floor floor)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = id, Point = new[] { x, y, 0 } }, new[] { floor });
        _state.Nodes[id] = node;
        return node;
    }

    private static void AddMeasurement(Node tx, Node rx, double distance)
    {
        tx.RxNodes[rx.Id] = new RxNode { Tx = tx, Rx = rx, Distance = distance, Rssi = -70, RefRssi = -59, LastHit = DateTime.UtcNow };
    }

    [Test]
    public void Sample_TracksSameFloorPairError()
    {
        var floor = MakeFloor("ground");
        var a = MakeNode("a", 0, 0, floor);
        var b = MakeNode("b", 4, 0, floor);
        AddMeasurement(a, b, 8); // map distance 4, measured 8 -> 100% error

        _tracker.Sample();

        var errors = _tracker.GetPairErrors();
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].Ewma, Is.EqualTo(1.0).Within(0.001), "first sample seeds the EWMA directly");
        Assert.That(errors[0].Samples, Is.EqualTo(1));
    }

    [Test]
    public void Sample_IgnoresCrossFloorPairs()
    {
        var ground = MakeFloor("ground");
        var first = MakeFloor("first");
        var a = MakeNode("a", 0, 0, ground);
        var b = MakeNode("b", 4, 0, first);
        AddMeasurement(a, b, 8);

        _tracker.Sample();

        Assert.That(_tracker.GetPairErrors(), Is.Empty, "cross-floor pairs carry no calibration signal and must not be tracked");
    }

    [Test]
    public void GetSuggestions_RequiresSustainedObservation()
    {
        var floor = MakeFloor("ground");
        var a = MakeNode("a", 0, 0, floor);
        var b = MakeNode("b", 4, 0, floor);
        AddMeasurement(a, b, 8);

        // Plenty of samples with 100% error - but all within this test's instant, far below the
        // 2h minimum observation span. Post-move calibration transients must not produce
        // suggestions, no matter how many samples accumulated quickly.
        for (var i = 0; i < PairErrorTracker.MinSamples + 10; i++) _tracker.Sample();

        Assert.That(_tracker.GetSuggestions(null), Is.Empty);
    }

    [Test]
    public void Sample_ResetsPairStatsWhenNodeMoves()
    {
        var floor = MakeFloor("ground");
        var a = MakeNode("a", 0, 0, floor);
        var b = MakeNode("b", 4, 0, floor);
        AddMeasurement(a, b, 8);

        _tracker.Sample();
        Assert.That(_tracker.GetPairErrors(), Has.Count.EqualTo(1));

        // Physically move node b - history against the old geometry is meaningless.
        b.Update(_configLoader.Config!, new ConfigNode { Id = "b", Name = "b", Point = new[] { 2.0, 3, 0 } }, new[] { floor });
        _tracker.Sample();

        var errors = _tracker.GetPairErrors();
        Assert.That(errors, Has.Count.EqualTo(1), "pair re-tracks after the reset");
        Assert.That(errors[0].Samples, Is.EqualTo(1), "stats restarted from scratch after the move");
    }
}
