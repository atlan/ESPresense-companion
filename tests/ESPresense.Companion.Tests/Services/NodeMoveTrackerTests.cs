using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Moving a node invalidates its history: walk measures recorded against the old geometry get
/// skipped and its pair statistics are dropped. Both already happened, both silently - nothing told
/// anyone a node had moved, which measurements had stopped counting, or why that node's calibration
/// suddenly wandered. This tracker exists to say so, and had no tests.
/// </summary>
public class NodeMoveTrackerTests
{
    private State _state = null!;
    private string _dir = null!;
    private ConfigLoader _configLoader = null!;
    private NodeSettingsStore _settings = null!;
    private Mock<IMqttCoordinator> _settingsMqtt = null!;
    private string _persistPath = null!;
    private Floor _floor = null!;

    [SetUp]
    public async Task Setup()
    {
        _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.yaml"), "mqtt:\n  host: localhost\n");
        _configLoader = new ConfigLoader(_dir);
        await _configLoader.ConfigAsync();
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        // The store only fills from retained MQTT settings, never from its own Set() - so a test
        // that wants a node to HAVE a calibration has to deliver it the way a node would.
        _settingsMqtt = new Mock<IMqttCoordinator>();
        _settings = new NodeSettingsStore(_settingsMqtt.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        await _settings.StartAsync(CancellationToken.None);
        _persistPath = Path.Combine(_dir, "node-moves.json");

        _floor = new Floor();
        _floor.Update(_configLoader.Config!, new ConfigFloor
        {
            Id = "ground", Name = "ground",
            Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 } }
        });
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private void PlaceNode(string id, double x, double y, double z = 2.4)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = id, Point = new[] { x, y, z } }, new[] { _floor });
        _state.Nodes[id] = node;
    }

    private void GivenSetting(string nodeId, string setting, string payload) =>
        _settingsMqtt.Raise(m => m.NodeSettingReceivedAsync += null,
            new ESPresense.Events.NodeSettingReceivedEventArgs { NodeId = nodeId, Setting = setting, Payload = payload });

    private void GivenAbsorptions(params (string id, double absorption)[] values)
    {
        foreach (var (id, absorption) in values)
            GivenSetting(id, "absorption", absorption.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private NodeMoveTracker MakeTracker() => new(_state, _settings, _persistPath);

    [Test]
    public async Task SaysNothingOnTheFirstPass()
    {
        // Everything looks new on a fresh install. Reporting each node as "moved" the first time the
        // service ever ran would bury the one real move under eighteen false ones.
        PlaceNode("a", 1, 1);
        var tracker = MakeTracker();

        await tracker.CheckAsync();

        Assert.That(tracker.GetMoves(), Is.Empty);
    }

    [Test]
    public async Task ReportsANodeThatChangedPosition()
    {
        PlaceNode("a", 1, 1);
        var tracker = MakeTracker();
        await tracker.CheckAsync();          // baseline
        PlaceNode("a", 3, 1);
        await tracker.CheckAsync();

        var moves = tracker.GetMoves();

        Assert.That(moves, Has.Count.EqualTo(1));
        Assert.That(moves[0].NodeId, Is.EqualTo("a"));
        Assert.That(moves[0].DistanceM, Is.EqualTo(2.0).Within(0.01));
        Assert.That(moves[0].From, Is.EqualTo(new[] { 1.0, 1.0, 2.4 }));
        Assert.That(moves[0].To, Is.EqualTo(new[] { 3.0, 1.0, 2.4 }));
    }

    [Test]
    public async Task IgnoresAChangeTooSmallToBeARelocation()
    {
        // Below the threshold this is a corrected typo in the map or mounting play, not a move.
        // Treating it as one would throw away that node's entire history for nothing.
        PlaceNode("a", 1, 1);
        var tracker = MakeTracker();
        await tracker.CheckAsync();
        PlaceNode("a", 1.02, 1);
        await tracker.CheckAsync();

        Assert.That(tracker.GetMoves(), Is.Empty);
    }

    [Test]
    public async Task ReseedsAbsorptionToTheFleetMedian()
    {
        // ★ Only absorption. rx_adj_rssi and tx_ref_rssi describe the HARDWARE - receive sensitivity
        // and transmit power - which carrying the node to another wall does not change. Clearing
        // them would discard valid information and start the next fit worse.
        foreach (var (id, x) in new[] { ("a", 1.0), ("b", 3.0), ("c", 5.0), ("d", 7.0) }) PlaceNode(id, x, 1);
        GivenAbsorptions(("a", 2.0), ("b", 4.0), ("c", 4.2), ("d", 4.4));
        GivenSetting("a", "rx_adj_rssi", "7");
        GivenSetting("a", "tx_ref_rssi", "-61");

        var tracker = MakeTracker();
        await tracker.CheckAsync();
        PlaceNode("a", 9, 9);
        await tracker.CheckAsync();

        // Checked through what was PUBLISHED, not through a read-back: NodeSettingsStore.Set writes
        // to MQTT and deliberately does not touch its own cache, which only ever fills from the
        // retained messages coming back. Asserting on Get() here would be testing the fixture.
        Assert.That(tracker.GetMoves()[0].AbsorptionReseededTo, Is.EqualTo(4.2).Within(0.01),
            "median of the other three nodes (4.0, 4.2, 4.4), not the value from the room it left");
        // Verified on the interface, since UpdateSetting is an extension method and Moq cannot see it.
        _settingsMqtt.Verify(m => m.EnqueueAsync("espresense/rooms/a/absorption/set", It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Once);
        _settingsMqtt.Verify(m => m.EnqueueAsync("espresense/rooms/a/rx_adj_rssi/set", It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never, "receive sensitivity describes the hardware and does not travel with the node");
        _settingsMqtt.Verify(m => m.EnqueueAsync("espresense/rooms/a/tx_ref_rssi/set", It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never, "neither does transmit power");
    }

    [Test]
    public async Task LeavesAbsorptionAloneWithoutAFleetToAgreeWith()
    {
        // Two other nodes are not a consensus. Re-seeding from them would replace a value fitted to
        // this node's surroundings with an average of almost nothing.
        PlaceNode("a", 1, 1); PlaceNode("b", 3, 1);
        GivenAbsorptions(("a", 2.0), ("b", 4.0));
        var tracker = MakeTracker();
        await tracker.CheckAsync();
        PlaceNode("a", 9, 9);
        await tracker.CheckAsync();

        Assert.That(_settings.Get("a").Calibration.Absorption, Is.EqualTo(2.0).Within(0.01));
        Assert.That(tracker.GetMoves()[0].AbsorptionReseededTo, Is.Null);
    }

    [Test]
    public async Task NoticesAMoveThatHappenedWhileItWasNotRunning()
    {
        // The reason the positions are written to disk at all: PairErrorTracker keeps its
        // last-known positions in memory only, so a node relocated during a restart is never noticed
        // there. A restart is also every deploy.
        PlaceNode("a", 1, 1);
        await MakeTracker().CheckAsync();

        PlaceNode("a", 6, 1);
        var afterRestart = MakeTracker();
        await afterRestart.CheckAsync();

        Assert.That(afterRestart.GetMoves(), Has.Count.EqualTo(1),
            "the baseline survived the restart, so this is a finding and not first-run noise");
        Assert.That(afterRestart.GetMoves()[0].DistanceM, Is.EqualTo(5.0).Within(0.01));
    }

    [Test]
    public async Task OnlyReportsMovesInsideTheAskedForWindow()
    {
        PlaceNode("a", 1, 1);
        var tracker = MakeTracker();
        await tracker.CheckAsync();
        PlaceNode("a", 6, 1);
        await tracker.CheckAsync();

        Assert.That(tracker.GetMoves(TimeSpan.FromMinutes(5)), Has.Count.EqualTo(1));
        Assert.That(tracker.GetMoves(TimeSpan.Zero), Is.Empty);
    }
}
