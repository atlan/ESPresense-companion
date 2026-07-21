using System.Text.Json;
using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class WalkTestServiceTests
{
    private State _state = null!;
    private string _configDir = null!;
    private ConfigLoader _configLoader = null!;
    private NodeSettingsStore _nodeSettings = null!;
    private string _persistPath = null!;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        var mqtt = new Mock<IMqttCoordinator>();
        _state = new State(_configLoader, new NodeTelemetryStore(mqtt.Object));
        _nodeSettings = new NodeSettingsStore(mqtt.Object, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        _persistPath = Path.Combine(_configDir, "walktest-points.json");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, true);
    }

    private WalkTestService MakeService() =>
        new(_state, new PairErrorTracker(_state), _nodeSettings, _persistPath);

    private void AddNode(string id, double x, double y, double z, string floorId = "ground")
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor { Id = floorId, Name = floorId, Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 } } });
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = id, Point = new[] { x, y, z } }, new[] { floor });
        _state.Nodes[id] = node;
    }

    private static WalkTestService.WalkTestPoint MakePoint(string id, double txRefEstimate = -70)
    {
        return new WalkTestService.WalkTestPoint
        {
            Id = id,
            DeviceId = "beacon",
            DeviceName = "Beacon",
            X = 2,
            Y = 2,
            Z = 1,
            FloorId = "ground",
            RecordedAt = DateTime.UtcNow,
            TxRefRssiEstimate = txRefEstimate,
            Nodes = new List<WalkTestService.NodeAggregate>
            {
                new()
                {
                    NodeId = "n1",
                    NodeName = "n1",
                    Samples = 10,
                    MedianDistance = 3.0,
                    MedianRssi = -80,
                    RefRssi = -59,
                    MapDistance = 2.83,
                    NodeLocX = 4, NodeLocY = 4, NodeLocZ = 1
                }
            }
        };
    }

    [Test]
    public void LoadPoints_RoundTripsPersistedPoints()
    {
        File.WriteAllText(_persistPath, JsonSerializer.Serialize(new List<WalkTestService.WalkTestPoint> { MakePoint("wt3") }));

        var service = MakeService();
        var points = service.GetPoints();

        Assert.That(points, Has.Count.EqualTo(1));
        Assert.That(points[0].Id, Is.EqualTo("wt3"));
        Assert.That(points[0].Nodes, Has.Count.EqualTo(1));
        Assert.That(points[0].TxRefRssiEstimate, Is.EqualTo(-70));
        Assert.That(points[0].Nodes[0].NodeLocationAtRecord.X, Is.EqualTo(4), "node location survives via plain-double fields");
    }

    [Test]
    public void GetExtraMeasures_ShiftsRssiToDefaultTxRef()
    {
        File.WriteAllText(_persistPath, JsonSerializer.Serialize(new List<WalkTestService.WalkTestPoint> { MakePoint("wt1", txRefEstimate: -70) }));
        AddNode("n1", 4, 4, 1);

        var service = MakeService();
        var measures = service.GetExtraMeasures();

        Assert.That(measures, Has.Count.EqualTo(1));
        // Estimate -70, evaluate default -59 -> shift +11: measured -80 becomes -69. Without this
        // shift every walk measure carries the estimate-vs-default delta as a constant error in
        // baseline evaluation.
        Assert.That(measures[0].Rssi, Is.EqualTo(-69).Within(0.001));
        Assert.That(measures[0].RefRssi, Is.EqualTo(-59));
        Assert.That(measures[0].Tx.Id, Is.EqualTo("walktest:beacon"));
        Assert.That(measures[0].Tx.FloorIds, Is.EqualTo(new[] { "ground" }), "floor ids must be set so IsCrossFloor filtering works");
    }

    [Test]
    public void GetExtraMeasures_DropsMeasuresWhoseNodeMoved()
    {
        File.WriteAllText(_persistPath, JsonSerializer.Serialize(new List<WalkTestService.WalkTestPoint> { MakePoint("wt1") }));
        AddNode("n1", 7, 7, 1); // recorded at (4,4,1) - node has since moved

        var service = MakeService();

        Assert.That(service.GetExtraMeasures(), Is.Empty, "RSSI captured against the old geometry must not feed the fit");
    }

    [Test]
    public void DeletePoint_PersistsRemoval()
    {
        File.WriteAllText(_persistPath, JsonSerializer.Serialize(new List<WalkTestService.WalkTestPoint> { MakePoint("wt1"), MakePoint("wt2") }));

        var service = MakeService();
        Assert.That(service.DeletePoint("wt1"), Is.True);

        // A fresh instance must not resurrect the deleted point.
        var reloaded = MakeService();
        Assert.That(reloaded.GetPoints().Select(p => p.Id), Is.EquivalentTo(new[] { "wt2" }));
    }
}
