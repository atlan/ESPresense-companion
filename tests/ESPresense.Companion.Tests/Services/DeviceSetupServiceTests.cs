using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// The guided 1 m reference measurement. It decides a device's rssi@1m, which cannot come out of the
/// node calibration - the nodes calibrate each other and a new tag is a stranger to all of them - so
/// a wrong answer here degrades that device's tracking everywhere and nothing else will correct it.
///
/// Driven through real MQTT device messages rather than a mocked capture, because the sampling,
/// grouping and median are the parts worth testing and a mock would replace exactly those.
/// </summary>
public class DeviceSetupServiceTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _dir = null!;
    private DeviceCaptureService _capture = null!;
    private DeviceSetupService _sut = null!;

    private const string Device = "ibeacon-osirisx";
    private const string RefNode = "kitchen";

    [SetUp]
    public async Task Setup()
    {
        _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.yaml"), "mqtt:\n  host: localhost\n");
        _configLoader = new ConfigLoader(_dir);
        await _configLoader.ConfigAsync();

        _mqtt = new Mock<IMqttCoordinator>();
        _state = new State(_configLoader, new NodeTelemetryStore(_mqtt.Object));
        _capture = new DeviceCaptureService(_mqtt.Object, _state);

        var deviceSettings = new DeviceSettingsStore(_mqtt.Object, _state);
        await deviceSettings.StartAsync(CancellationToken.None);

        _sut = new DeviceSetupService(_state, _capture, deviceSettings, new DeviceIdentityTracker(_mqtt.Object));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private void AddNode(string id, string name)
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor
        {
            Id = "ground", Name = "ground",
            Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 } }
        });
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = name, Point = new[] { 1.0, 1.0, 2.4 } }, new[] { floor });
        _state.Nodes[id] = node;
    }

    /// <summary>Delivers readings the way a node does: raw level plus its own receive adjustment.</summary>
    private void Hear(string nodeId, double rssi, int count, double rxAdj = 0)
    {
        for (var i = 0; i < count; i++)
            _mqtt.Raise(m => m.DeviceMessageReceivedAsync += null,
                new DeviceMessageEventArgs
                {
                    DeviceId = Device,
                    NodeId = nodeId,
                    Payload = new DeviceMessage { Rssi = rssi, RssiRxAdj = rxAdj, RefRssi = -59, Distance = 1.0 }
                });
    }

    private ReferenceStatus RunOnce(double rssi, double distanceM = 1.0, int samples = 10)
    {
        _sut.StartReference(Device, RefNode, distanceM);
        Hear(RefNode, rssi, samples);
        return _sut.FinishReference();
    }

    [Test]
    public void AtOneMetreTheMeasuredLevelIsTheAnswer()
    {
        // The whole reason the measurement is taken at 1 m: log10(1) is 0, so the absorption term
        // drops out entirely and nothing about the node's fit enters the result.
        AddNode(RefNode, "Kitchen Node");

        var status = RunOnce(rssi: -77);

        Assert.That(status.EstimatedRefRssi, Is.EqualTo(-77));
    }

    [Test]
    public void AddsTheNodesOwnReceiveAdjustmentBackIn()
    {
        // The node reports the raw level and its adjustment separately, and adjustments span
        // -5..+25 dB across a fleet. Without adding it back the answer would describe the node as
        // much as the device - and the device is the thing being measured.
        AddNode(RefNode, "Kitchen Node");
        _sut.StartReference(Device, RefNode, 1.0);
        Hear(RefNode, rssi: -85, count: 10, rxAdj: 8);

        Assert.That(_sut.FinishReference().EstimatedRefRssi, Is.EqualTo(-77));
    }

    [Test]
    public void ProjectsBackToOneMetreWhenMeasuredFurtherAway()
    {
        // Sometimes 1 m is not possible. The projection uses a plain free-space exponent on purpose,
        // not the node's fitted absorption - the fitted value is what this measurement exists to be
        // independent of.
        AddNode(RefNode, "Kitchen Node");

        var status = RunOnce(rssi: -83, distanceM: 2.0);

        // -83 + 10 * 2.0 * log10(2) = -83 + 6.02
        Assert.That(status.EstimatedRefRssi, Is.EqualTo(-77));
    }

    [Test]
    public void OneRunIsProvisionalNoMatterHowCleanItLooks()
    {
        // Repeating the identical placement scatters by about 4.4 dB - orientation, hand position,
        // exact distance. A single reading cannot separate the device from how it was held, however
        // many samples it contains.
        AddNode(RefNode, "Kitchen Node");

        var status = RunOnce(rssi: -77, samples: 50);

        Assert.That(status.Trusted, Is.False);
        Assert.That(status.Runs, Is.EqualTo(1));
        Assert.That(status.Warning, Does.Contain("Provisional"));
    }

    [Test]
    public void TwoAgreeingRunsAreTrusted()
    {
        AddNode(RefNode, "Kitchen Node");
        RunOnce(rssi: -77);
        var status = RunOnce(rssi: -78);

        Assert.That(status.Runs, Is.EqualTo(2));
        Assert.That(status.Trusted, Is.True);
        Assert.That(status.Warning, Is.Null);
        Assert.That(status.EstimatedRefRssi, Is.EqualTo(-78).Within(1));
    }

    [Test]
    public void TwoRunsThatDisagreeAreNotTrusted()
    {
        // Beyond placement scatter something actually changed between the runs, and averaging two
        // measurements of two different situations produces a number describing neither.
        AddNode(RefNode, "Kitchen Node");
        RunOnce(rssi: -77);
        var status = RunOnce(rssi: -90);

        Assert.That(status.Trusted, Is.False);
        Assert.That(status.RunSpreadDb, Is.EqualTo(13).Within(0.5));
        Assert.That(status.Warning, Does.Contain("differ"));
    }

    [Test]
    public void ResetForgetsPreviousRuns()
    {
        // For when the tag itself was swapped: the old runs describe different hardware, and keeping
        // them would make the new one look like a wild disagreement with itself.
        AddNode(RefNode, "Kitchen Node");
        RunOnce(rssi: -77);
        _sut.ResetRuns(Device);
        var status = RunOnce(rssi: -90);

        Assert.That(status.Runs, Is.EqualTo(1));
    }

    [Test]
    public void ComplainsWhenTheReferenceNodeNeverHeardTheDevice()
    {
        AddNode(RefNode, "Kitchen Node");
        AddNode("floor", "Floor Node");
        _sut.StartReference(Device, RefNode, 1.0);
        Hear("floor", -80, 10);

        var status = _sut.FinishReference();

        Assert.That(status.EstimatedRefRssi, Is.Null);
        Assert.That(status.Warning, Does.Contain("did not report the device"));
    }

    [Test]
    public void AsksForLongerWhenTooFewReadingsArrived()
    {
        AddNode(RefNode, "Kitchen Node");
        _sut.StartReference(Device, RefNode, 1.0);
        Hear(RefNode, -77, count: 3);

        var status = _sut.FinishReference();

        Assert.That(status.Warning, Does.Contain("Let it run longer"));
    }

    [Test]
    public void FlagsANodeThatHearsLouderThanTheOneBeingHeldAgainst()
    {
        // A node metres away reading stronger than the one the device is touching is saying
        // something impossible about itself - either far more sensitive than its adjustment claims,
        // or not where the map puts it. Cheap to catch now, expensive to find a week later.
        AddNode(RefNode, "Kitchen Node");
        AddNode("floor", "Floor Node");
        _sut.StartReference(Device, RefNode, 1.0);
        Hear(RefNode, -77, 10);
        Hear("floor", -60, 10);

        var status = _sut.FinishReference();

        Assert.That(status.ContextNote, Does.Contain("Floor Node"));
        Assert.That(status.ContextNote, Does.Contain("louder"));
    }

    [Test]
    public void ReportsEveryNodeThatHeardIt()
    {
        // Not just the reference one. The others cost nothing to record and are what makes the
        // implausible-node check above possible at all.
        AddNode(RefNode, "Kitchen Node");
        AddNode("floor", "Floor Node");
        AddNode("pantry", "Pantry Node");
        _sut.StartReference(Device, RefNode, 1.0);
        Hear(RefNode, -77, 10);
        Hear("floor", -88, 10);
        Hear("pantry", -95, 10);

        var status = _sut.FinishReference();

        Assert.That(status.Nodes.Select(n => n.NodeId), Is.EquivalentTo(new[] { RefNode, "floor", "pantry" }));
        Assert.That(status.Nodes.First().IsReference, Is.True, "the reference node leads the list");
    }

    [Test]
    public void StatusReportsARunInProgress()
    {
        AddNode(RefNode, "Kitchen Node");
        _sut.StartReference(Device, RefNode, 1.0);
        Hear(RefNode, -77, 10);

        var status = _sut.Status();

        Assert.That(status.Running, Is.True);
        Assert.That(status.DeviceId, Is.EqualTo(Device));
        Assert.That(status.EstimatedRefRssi, Is.EqualTo(-77), "a live estimate while it is still collecting");
    }

    [Test]
    public void CancelStopsWithoutRecordingARun()
    {
        AddNode(RefNode, "Kitchen Node");
        RunOnce(rssi: -77);

        _sut.StartReference(Device, RefNode, 1.0);
        Hear(RefNode, -90, 10);
        _sut.CancelReference();

        Assert.That(_sut.Status().Running, Is.False);

        var next = RunOnce(rssi: -78);
        Assert.That(next.Runs, Is.EqualTo(2), "the cancelled attempt must not count as one of the runs");
    }
}
