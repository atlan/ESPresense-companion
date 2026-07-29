using ESPresense.Models;
using ESPresense.Locators;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// The rule these replace took its height from the average of two node mountings, which on the real
/// installation range from +0.30 m to +2.20 m above the floor - so the suggested height was wherever
/// two boxes happened to be screwed, not where a device is carried. That is the first thing pinned
/// down here: the height comes from measured walk points, never from node positions.
/// </summary>
public class WalkPointPlannerTests
{
    private State _state = null!;
    private string _dir = null!;
    private ConfigLoader _configLoader = null!;
    private WalkTestService _walkTest = null!;
    private CalibrationBenchmark _benchmark = null!;
    private string _pointsPath = null!;

    private const double CeilingZ = 2.4;

    [SetUp]
    public async Task Setup()
    {
        _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.yaml"), "mqtt:\n  host: localhost\nlocators:\n  nadaraya_watson:\n    enabled: true\n");
        _configLoader = new ConfigLoader(_dir);
        await _configLoader.ConfigAsync();
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));

        _pointsPath = Path.Combine(_dir, "walktest-points.json");
        _walkTest = new WalkTestService(_state, new PairErrorTracker(_state),
            new NodeSettingsStore(new Mock<IMqttCoordinator>().Object,
                Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>()),
            _pointsPath);
        _benchmark = new CalibrationBenchmark(_state, _walkTest, _configLoader, new ScenarioReplay(_state, _configLoader),
            new NodeSettingsStore(new Mock<IMqttCoordinator>().Object,
                Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>()),
            Path.Combine(_dir, "benchmark.json"));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>One floor with two square rooms side by side, and a node above each.</summary>
    private Floor AddFloor(string id, double zMin, double zMax)
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor
        {
            Id = id,
            Name = id,
            Bounds = new[] { new[] { 0.0, 0, zMin }, new[] { 12.0, 6, zMax } },
            Rooms = new[]
            {
                new ConfigRoom
                {
                    Id = $"{id}_west", Name = $"{id} West",
                    Points = new[] { new[] { 0.0, 0.0 }, new[] { 5.0, 0.0 }, new[] { 5.0, 5.0 }, new[] { 0.0, 5.0 } }
                },
                new ConfigRoom
                {
                    Id = $"{id}_east", Name = $"{id} East",
                    Points = new[] { new[] { 6.0, 0.0 }, new[] { 11.0, 0.0 }, new[] { 11.0, 5.0 }, new[] { 6.0, 5.0 } }
                }
            }
        });
        _state.Floors[id] = floor;
        return floor;
    }

    private void AddNode(string id, double x, double y, Floor floor, double z = CeilingZ)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = id, Point = new[] { x, y, z } }, new[] { floor });
        _state.Nodes[id] = node;
    }

    private WalkPointPlanner MakePlanner() => new(_state, _walkTest, _benchmark);

    /// <summary>Seeds walk points through the persistence file - no test-only hook in the service.</summary>
    private void GivenWalkPoint(string floorId, double x, double y, double z)
    {
        var point = new WalkTestService.WalkTestPoint
        {
            Id = $"wt{Guid.NewGuid():N}"[..6], DeviceId = "beacon", DeviceName = "Beacon",
            X = x, Y = y, Z = z, FloorId = floorId, RecordedAt = DateTime.UtcNow
        };
        File.WriteAllText(_pointsPath,
            System.Text.Json.JsonSerializer.Serialize(new List<WalkTestService.WalkTestPoint> { point }));
        _walkTest = new WalkTestService(_state, new PairErrorTracker(_state),
            new NodeSettingsStore(new Mock<IMqttCoordinator>().Object,
                Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>()),
            _pointsPath);
    }

    [Test]
    public void Suggest_TakesHeightFromWalkPointsNotFromNodeMountings()
    {
        var floor = AddFloor("ground", 0, 3);
        AddNode("n1", 1, 1, floor);
        AddNode("n2", 4, 4, floor);
        AddNode("n3", 8, 1, floor);
        AddNode("n4", 10, 4, floor);

        var suggestions = MakePlanner().Suggest();

        Assert.That(suggestions, Is.Not.Empty);
        Assert.That(suggestions.Select(s => s.Z), Is.All.EqualTo(1.0),
            "with no walk points on the floor the device height falls back to 1 m, never to the " +
            $"node mounting height ({CeilingZ} m here) - the previous rule averaged node coordinates");
    }

    [Test]
    public void Suggest_PutsThePointInsideTheRoomItNames()
    {
        var floor = AddFloor("ground", 0, 3);
        AddNode("n1", 1, 1, floor);
        AddNode("n2", 4, 4, floor);
        AddNode("n3", 8, 1, floor);
        AddNode("n4", 10, 4, floor);

        foreach (var s in MakePlanner().Suggest(4))
        {
            var room = _state.Floors[s.FloorId!].Rooms[s.RoomId!];
            Assert.That(room.Polygon!.EnclosesPoint(new Point2D(s.X, s.Y)), Is.True,
                $"suggestion for '{s.RoomName}' landed outside it - a midpoint between two nodes can " +
                "fall in a wall, which is what this rule replaces");
        }
    }

    [Test]
    public void Suggest_PrefersTheRoomNobodyHasMeasured()
    {
        var floor = AddFloor("ground", 0, 3);
        AddNode("n1", 1, 1, floor);
        AddNode("n2", 4, 4, floor);
        AddNode("n3", 8, 1, floor);
        AddNode("n4", 10, 4, floor);
        // West is covered by an existing walk point; east has none.
        GivenWalkPoint("ground", 2.5, 2.5, 1.1);

        var first = MakePlanner().Suggest().First();

        Assert.That(first.RoomId, Is.EqualTo("ground_east"));
        Assert.That(first.ExistingPoints, Is.Zero);
        Assert.That(first.Reason, Does.Contain("never been measured"));
    }

    [Test]
    public void Suggest_TakesTheDeviceHeightFromExistingWalkPoints()
    {
        var floor = AddFloor("ground", 0, 3);
        AddNode("n1", 1, 1, floor);
        AddNode("n2", 4, 4, floor);
        AddNode("n3", 8, 1, floor);
        GivenWalkPoint("ground", 2.5, 2.5, 0.85);

        Assert.That(MakePlanner().Suggest().First().Z, Is.EqualTo(0.85),
            "measured beats guessed - the device is carried at the height it was carried at before");
    }

    [Test]
    public void Suggest_SpreadsAcrossFloorsBeforeGoingDeepOnOne()
    {
        var ground = AddFloor("ground", 0, 3);
        var upper = AddFloor("upper", 3, 6);
        AddNode("g1", 1, 1, ground);
        AddNode("g2", 4, 4, ground);
        AddNode("g3", 8, 1, ground);
        // Upper is deliberately worse covered, so scoring alone would hand it every slot.
        AddNode("u1", 0.5, 0.5, upper, 5.4);
        AddNode("u2", 1.0, 0.5, upper, 5.4);
        AddNode("u3", 0.5, 1.0, upper, 5.4);

        var floors = MakePlanner().Suggest(2).Select(s => s.FloorId).ToList();

        Assert.That(floors, Is.Unique,
            "a walk test confined to one storey cannot separate a floor-detection problem from a " +
            "coverage problem, so the first round takes the best room per floor");
    }
}
