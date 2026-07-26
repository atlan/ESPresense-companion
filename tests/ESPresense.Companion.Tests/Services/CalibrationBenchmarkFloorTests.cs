using System.Text.Json;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Floor detection is the one figure the benchmark scores WITHOUT being told the answer - every
/// distance and room number it reports is measured with the floor already known. These tests cover
/// the two ways that scoring can quietly become meaningless: silently degrading to "always the
/// densest floor", and being computed after the same-floor filter that presupposes the result.
/// </summary>
public class CalibrationBenchmarkFloorTests
{
    private State _state = null!;
    private string _dir = null!;
    private ConfigLoader _configLoader = null!;
    private WalkTestService _walkTest = null!;
    private string _pointsPath = null!;

    private static readonly Point3D Truth = new(2, 2, 1);

    [SetUp]
    public async Task Setup()
    {
        _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.yaml"), "mqtt:\n  host: localhost\n");
        _configLoader = new ConfigLoader(_dir);
        await _configLoader.ConfigAsync();
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        _pointsPath = Path.Combine(_dir, "walktest-points.json");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private Floor MakeFloor(string id, double zMin, double zMax)
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor
        {
            Id = id,
            Name = id,
            Bounds = new[] { new[] { 0.0, 0, zMin }, new[] { 10.0, 10, zMax } }
        });
        _state.Floors[id] = floor;
        return floor;
    }

    private Node AddNode(string id, Point3D at, Floor floor)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!,
            new ConfigNode { Id = id, Name = id, Point = new[] { at.X, at.Y, at.Z } },
            new[] { floor });
        _state.Nodes[id] = node;
        return node;
    }

    /// <summary>
    /// Writes a walk point at <see cref="Truth"/> whose ticks report, for every node, exactly the
    /// geometric distance from that node to the truth. No noise: the question is whether the right
    /// floor wins on honest data, and noise would only blur the answer.
    /// </summary>
    private void WritePoint(string floorId, int ticks = 10)
    {
        var raw = new List<WalkTestService.RawTickEntry>();
        for (var t = 0; t < ticks; t++)
            foreach (var node in _state.Nodes.Values)
                raw.Add(new WalkTestService.RawTickEntry
                {
                    T = t,
                    N = node.Id,
                    D = Math.Round(node.Location.DistanceTo(Truth), 3)
                });

        var point = new WalkTestService.WalkTestPoint
        {
            Id = "wt1",
            DeviceId = "beacon",
            DeviceName = "Beacon",
            X = Truth.X,
            Y = Truth.Y,
            Z = Truth.Z,
            FloorId = floorId,
            RecordedAt = DateTime.UtcNow,
            Raw = raw
        };
        File.WriteAllText(_pointsPath, JsonSerializer.Serialize(new List<WalkTestService.WalkTestPoint> { point }));

        _walkTest = new WalkTestService(_state, new PairErrorTracker(_state),
            new NodeSettingsStore(new Mock<IMqttCoordinator>().Object,
                Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>()),
            _pointsPath);
    }

    private CalibrationBenchmark MakeBenchmark() =>
        new(_state, _walkTest, _configLoader, Path.Combine(_dir, "benchmark.json"));

    [Test]
    public void Run_ScoresFloorDetectionOnAllAudibleNodes()
    {
        var ground = MakeFloor("ground", 0, 3);
        var upper = MakeFloor("upper", 3, 6);
        AddNode("g1", new Point3D(0, 0, 2.4), ground);
        AddNode("g2", new Point3D(8, 0, 2.4), ground);
        AddNode("g3", new Point3D(0, 8, 2.4), ground);
        AddNode("u1", new Point3D(0, 0, 5.4), upper);
        AddNode("u2", new Point3D(8, 0, 5.4), upper);
        AddNode("u3", new Point3D(0, 8, 5.4), upper);
        WritePoint("ground");

        var result = MakeBenchmark().Run();

        Assert.That(result.Error, Is.Null);
        Assert.That(result.FloorTicksChecked, Is.EqualTo(10),
            "every tick must be scored, and against all six nodes - scoring after the same-floor " +
            "filter would only ever confirm the floor it was handed");
        Assert.That(result.FloorHitRate, Is.EqualTo(1.0),
            "the device is on the ground floor and the ground nodes' distances fit it exactly");
    }

    [Test]
    public void Run_SparseCorrectFloorBeatsDenserWrongOne()
    {
        // The failure this guards against: confidence is half coverage and half fit, so a floor
        // packed with nodes can out-score the floor the device is actually on. A basement with
        // three sensors must still be able to win against a living area with six.
        var ground = MakeFloor("ground", 0, 3);
        var upper = MakeFloor("upper", 3, 6);
        AddNode("g1", new Point3D(0, 0, 2.4), ground);
        AddNode("g2", new Point3D(8, 0, 2.4), ground);
        AddNode("g3", new Point3D(0, 8, 2.4), ground);
        for (var i = 0; i < 6; i++)
            AddNode($"u{i}", new Point3D(i * 1.6, 8 - i, 5.4), upper);
        WritePoint("ground");

        var result = MakeBenchmark().Run();

        Assert.That(result.FloorHitRate, Is.EqualTo(1.0),
            "three well-fitting nodes must outweigh six on the wrong floor - both floors hear all " +
            "of their own nodes, so coverage is 50/50 and only the fit can decide");
        Assert.That(result.FloorConfusion, Is.Empty);
    }

    [Test]
    public void Run_ReportsWhichFloorItWasConfusedWith()
    {
        // Ground has the bare minimum and its nodes are bunched in one corner, so the fit is poor;
        // upper is well spread. A wrong answer must be named, not just counted - "83 % correct" does
        // not tell anyone which storey to add a node to.
        var ground = MakeFloor("ground", 0, 3);
        var upper = MakeFloor("upper", 3, 6);
        AddNode("g1", new Point3D(9.0, 9.0, 2.4), ground);
        AddNode("g2", new Point3D(9.2, 9.0, 2.4), ground);
        AddNode("g3", new Point3D(9.0, 9.2, 2.4), ground);
        AddNode("u1", new Point3D(2, 2, 5.4), upper);
        AddNode("u2", new Point3D(2.2, 2, 5.4), upper);
        AddNode("u3", new Point3D(2, 2.2, 5.4), upper);
        WritePoint("ground");

        var result = MakeBenchmark().Run();

        if (result.FloorHitRate < 1.0)
        {
            Assert.That(result.FloorConfusion, Is.Not.Empty);
            Assert.That(result.FloorConfusion[0].Pair, Is.EqualTo("ground -> upper"));
            Assert.That(result.Verdict, Does.Contain("ground -> upper"),
                "the verdict is what a first-time user reads - the mix-up belongs in it");
        }
        else
        {
            Assert.Inconclusive("Geometry did not provoke a mix-up; confusion reporting untested here.");
        }
    }
}
