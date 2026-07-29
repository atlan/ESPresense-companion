using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// The measurement-level diagnostics: 602 lines that a newcomer reads to find out what is wrong with
/// their installation, and which had never been executed by a test.
///
/// What makes them worth testing is that they are the only thing standing between a user and an
/// afternoon of guessing. Every check here exists because a real investigation needed raw MQTT, the
/// calibration matrix and hand arithmetic to find something the UI could simply have said.
/// </summary>
public class WizardDiagnosticsTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _dir = null!;
    private NodeSettingsStore _nodeSettings = null!;
    private WalkTestService _walkTest = null!;
    private NodeMoveTracker _moves = null!;
    private DeviceIdentityTracker _identities = null!;
    private WizardDiagnostics _sut = null!;
    private Floor _floor = null!;

    [SetUp]
    public async Task Setup()
    {
        _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.yaml"), @"mqtt:
  host: localhost
optimization:
  enabled: true
  limits:
    absorption_min: 2.5
    absorption_max: 4.8
    rx_adj_rssi_min: -5
    rx_adj_rssi_max: 25
    tx_ref_rssi_min: -80
    tx_ref_rssi_max: -40
floors:
- id: ground
  name: Ground
  bounds:
  - [0, 0, 0]
  - [12, 12, 3]
  rooms:
  - id: hall
    name: Hall
    points: [[0, 0], [12, 0], [12, 12], [0, 12]]
");
        _configLoader = new ConfigLoader(_dir);
        await _configLoader.ConfigAsync();

        _mqtt = new Mock<IMqttCoordinator>();
        _state = new State(_configLoader, new NodeTelemetryStore(_mqtt.Object));
        _nodeSettings = new NodeSettingsStore(_mqtt.Object, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        await _nodeSettings.StartAsync(CancellationToken.None);

        var pairErrors = new PairErrorTracker(_state);
        _walkTest = new WalkTestService(_state, pairErrors, _nodeSettings, Path.Combine(_dir, "walk.json"));
        _moves = new NodeMoveTracker(_state, _nodeSettings, Path.Combine(_dir, "moves.json"));
        _identities = new DeviceIdentityTracker(_mqtt.Object);

        _floor = new Floor();
        _floor.Update(_configLoader.Config!, _configLoader.Config!.Floors!.First());
        _state.Floors["ground"] = _floor;

        _sut = new WizardDiagnostics(_state, _nodeSettings, _configLoader, _identities, _moves, _walkTest, pairErrors);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private Node AddNode(string id, double x, double y, double z = 2.4)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = id, Point = new[] { x, y, z } }, new[] { _floor });
        _state.Nodes[id] = node;
        return node;
    }

    /// <summary>
    /// Makes rx hear tx at the given level. The snapshot only contains readings younger than 30 s,
    /// so LastHit has to be now - a fixture that forgets this produces an empty snapshot and every
    /// signal assertion silently passes for the wrong reason.
    /// </summary>
    private void Hears(Node rx, Node tx, double rssi, double distance)
    {
        tx.RxNodes[rx.Id] = new RxNode
        {
            Tx = tx, Rx = rx,
            Rssi = rssi, RefRssi = -59, Distance = distance,
            RssiVar = 1.0, LastHit = DateTime.UtcNow, Hits = 10
        };
    }

    private void GivenSetting(string nodeId, string setting, string payload) =>
        _mqtt.Raise(m => m.NodeSettingReceivedAsync += null,
            new NodeSettingReceivedEventArgs { NodeId = nodeId, Setting = setting, Payload = payload });

    /// <summary>
    /// A 2 m grid across the whole room, so every spot has a node within about 1.4 m. Nine nodes in
    /// a 12x12 room sounds generous and is not - most of the floor is then over 1.5 m from anything,
    /// and the coverage check is right to say so. Written out because the first version of these
    /// tests got that wrong.
    /// </summary>
    private List<Node> AddGrid()
    {
        var nodes = new List<Node>();
        for (var x = 1.0; x < 12; x += 2)
        for (var y = 1.0; y < 12; y += 2)
            nodes.Add(AddNode($"n{x:0}_{y:0}", x, y));
        return nodes;
    }

    /// <summary>Runs Analyze often enough for the signal smoothing to have something to say.</summary>
    private WizardDiagnosticsResult AnalyzeSettled(int times = 4)
    {
        WizardDiagnosticsResult result = null!;
        for (var i = 0; i < times; i++) result = _sut.Analyze();
        return result;
    }

    // ---------------------------------------------------------------- coverage

    [Test]
    public void MeasuresHowFarEachRoomIsFromItsNearestNode()
    {
        // The strongest predictor of accuracy found on the real installation, and the one thing a
        // user can act on directly: within 1.5 m spots measured 1.11 m median error, beyond 2.47 m.
        AddNode("corner", 0.5, 0.5);

        var coverage = _sut.Analyze().RoomCoverage.Single();

        Assert.That(coverage.RoomId, Is.EqualTo("hall"));
        Assert.That(coverage.MedianNearestNodeM, Is.GreaterThan(5),
            "one node in the corner of a 12x12 room leaves most of it far away");
        Assert.That(coverage.WellCoveredFraction, Is.LessThan(0.1));
    }

    [Test]
    public void SamplesTheRoomRatherThanItsCentre()
    {
        // A node in one corner of a long room leaves the far end as uncovered as no node at all,
        // and a centroid-based check would call that room perfectly served.
        AddNode("corner", 0.5, 0.5);

        var coverage = _sut.Analyze().RoomCoverage.Single();

        Assert.That(coverage.SampledPoints, Is.GreaterThan(100));
        Assert.That(coverage.WorstNearestNodeM, Is.GreaterThan(coverage.MedianNearestNodeM));
    }

    [Test]
    public void SaysWhatAccuracyToExpectAndWhatToDoAboutIt()
    {
        AddNode("corner", 0.5, 0.5);

        var issue = _sut.Analyze().Issues.Single(i => i.Category == "coverage");

        Assert.That(issue.RoomId, Is.EqualTo("hall"));
        // Die Meldung ist seit dem 29.07.2026 deutsch (Dezimalkomma!). Geprueft wird weiterhin
        // dasselbe: nennt sie die GEMESSENE Erwartung und die Handlung - statt vage zu warnen.
        Assert.That(issue.Message, Does.Contain("2,5 m"), "die gemessene Erwartung, keine vage Warnung");
        Assert.That(issue.Message, Does.Contain("zusätzlicher Knoten"));
    }

    [Test]
    public void SaysNothingAboutAWellCoveredRoom()
    {
        AddGrid();

        // 0.94 rather than 1.0, and correctly so: the check measures in three dimensions, and at the
        // very corners the nearest node is 1.41 m away horizontally plus 0.9 m of ceiling height,
        // which lands just past 1.5 m. Well clear of the threshold that raises an issue.
        var coverage = _sut.Analyze().RoomCoverage.Single();
        Assert.That(coverage.WellCoveredFraction, Is.GreaterThan(0.9));
        Assert.That(_sut.Analyze().Issues.Any(i => i.Category == "coverage"), Is.False);
    }

    // ---------------------------------------------------------------- clamped parameters

    [Test]
    public void ReportsAParameterSittingOnItsConfiguredLimit()
    {
        // A value resting exactly on its bound means the optimizer wanted to go further and was not
        // allowed to. The fit is then not "the best possible" but "the best inside a box that
        // excludes the answer" - and nothing else in the UI says so.
        AddNode("pantry", 6, 6);
        GivenSetting("pantry", "rx_adj_rssi", "25");

        var issue = _sut.Analyze().Issues.Single(i => i.Category == "clamped");

        Assert.That(issue.NodeId, Is.EqualTo("pantry"));
        Assert.That(issue.Message, Does.Contain("max"));
        // Sagt sie, WAS zu tun ist? (deutsch seit dem 29.07.2026)
        Assert.That(issue.Message, Does.Contain("Erweitere"));
    }

    [Test]
    public void IgnoresAParameterComfortablyInsideItsLimits()
    {
        AddNode("pantry", 6, 6);
        GivenSetting("pantry", "rx_adj_rssi", "10");
        GivenSetting("pantry", "absorption", "3.5");

        Assert.That(_sut.Analyze().ClampedParameters, Is.Empty);
    }

    // ---------------------------------------------------------------- split identities

    private void GivenTrackedDevice(string id)
    {
        _state.Devices[id] = new Device(id, null, TimeSpan.FromSeconds(30)) { Track = true };
    }

    private void SeenUnder(string deviceId, string mac, string nodeId = "floor") =>
        _mqtt.Raise(m => m.DeviceMessageReceivedAsync += null,
            new DeviceMessageEventArgs { DeviceId = deviceId, NodeId = nodeId, Payload = new DeviceMessage { Mac = mac } });

    [Test]
    public void ReportsOneAddressReportingUnderTwoDeviceIds()
    {
        AddNode("floor", 6, 6);
        GivenTrackedDevice("beacon-a");
        SeenUnder("beacon-a", "aabbcc");
        SeenUnder("beacon-b", "aabbcc");

        var issue = _sut.Analyze().Issues.Single(i => i.Category == "identity");

        Assert.That(issue.Severity, Is.EqualTo(ValidationSeverity.Error));
        Assert.That(issue.Message, Does.Contain("espresense/settings/"), "the message has to carry the fix, not just the fault");
    }

    [Test]
    public void SaysNothingAboutTwoDevicesNobodyTracks()
    {
        // Every phone and wearable in the house shares and rotates addresses. Reporting those is a
        // list of facts rather than a list of problems, and it buries the one finding that matters.
        AddNode("floor", 6, 6);
        SeenUnder("md:05fe:20", "68649e");
        SeenUnder("name:ccc3", "68649e");

        Assert.That(_sut.Analyze().Issues.Any(i => i.Category == "identity"), Is.False);
    }

    [Test]
    public void AliasesOntoTheTrackedIdEvenWhenAnotherWasHeardByMoreNodes()
    {
        // Ordering by node count alone would suggest aliasing the tracked device onto an untracked
        // one - moving the measurements somewhere nothing is looking.
        AddNode("floor", 6, 6);
        AddNode("kitchen", 2, 2);
        AddNode("pantry", 9, 2);
        GivenTrackedDevice("beacon-a");
        SeenUnder("beacon-a", "aabbcc", "floor");
        foreach (var n in new[] { "floor", "kitchen", "pantry" }) SeenUnder("raw-mac-id", "aabbcc", n);

        var issue = _sut.Analyze().Issues.Single(i => i.Category == "identity");

        Assert.That(issue.Message, Does.Contain("{\"id\":\"beacon-a\"}"),
            "the tracked device is the target, however few nodes reached it");
    }

    // ---------------------------------------------------------------- node moves

    [Test]
    public async Task ReportsARelocatedNodeAndWhatItCost()
    {
        AddNode("floor", 2, 2);
        await _moves.CheckAsync();
        AddNode("floor", 9, 9);
        await _moves.CheckAsync();

        var issue = _sut.Analyze().Issues.Single(i => i.Category == "moved");

        Assert.That(issue.NodeId, Is.EqualTo("floor"));
        Assert.That(issue.Message, Does.Contain("pair-error history was reset"));
    }

    // ---------------------------------------------------------------- signal plausibility

    [Test]
    public void ReportsAPairNoPathLossSettingCanExplain()
    {
        // Two nodes 6 m apart, one hearing the other 25 dB louder than the model allows. That is not
        // a calibration problem - averaging it into an error figure hides exactly what is needed.
        var a = AddNode("a", 1, 1);
        var b = AddNode("b", 7, 1);
        GivenSetting("a", "absorption", "3.0");
        // Required at 6 m with absorption 3: -59 - 30*log10(6) = -82.3. Heard at -57.
        Hears(a, b, rssi: -57, distance: 1.0);

        var result = AnalyzeSettled();

        Assert.That(result.Issues.Any(i => i.Category == "signal" && i.NodeId == "a"), Is.True);
        var outlier = result.SignalOutliers.Single();
        Assert.That(outlier.DeltaDb, Is.GreaterThan(15));
        Assert.That(outlier.Reported, Is.True);
    }

    [Test]
    public void StaysQuietUntilItHasSeenAPairMoreThanOnce()
    {
        // One stray packet is not a finding. Reporting on the first sample is what made the list
        // reshuffle between polls before the smoothing existed.
        var a = AddNode("a", 1, 1);
        var b = AddNode("b", 7, 1);
        GivenSetting("a", "absorption", "3.0");
        Hears(a, b, rssi: -57, distance: 1.0);

        Assert.That(_sut.Analyze().Issues.Any(i => i.Category == "signal"), Is.False,
            "first look, nothing said");
        Assert.That(AnalyzeSettled().Issues.Any(i => i.Category == "signal"), Is.True);
    }

    [Test]
    public void KeepsReportingAPairThatDropsBackToTheGreyZone()
    {
        // Hysteresis. Measured before it existed: eight of thirty-nine findings flipped in and out
        // across three requests twelve seconds apart, because pairs sitting near the threshold
        // crossed it either way. A list that reshuffles while it is being read cannot be worked through.
        var a = AddNode("a", 1, 1);
        var b = AddNode("b", 7, 1);
        GivenSetting("a", "absorption", "3.0");

        Hears(a, b, rssi: -57, distance: 1.0);      // ~25 dB off, well over the threshold
        AnalyzeSettled();

        // Now drift to about 13 dB - below the reporting threshold, above the release one.
        for (var i = 0; i < 20; i++)
        {
            Hears(a, b, rssi: -69, distance: 1.0);
            _sut.Analyze();
        }

        var outlier = _sut.Analyze().SignalOutliers.Single();
        Assert.That(Math.Abs(outlier.DeltaDb), Is.LessThan(15).And.GreaterThan(12));
        Assert.That(outlier.Reported, Is.True, "latched on above 15 dB, released only below 12");
    }

    [Test]
    public void SmoothsTheReportedLevelInsteadOfEchoingTheLatestSample()
    {
        // The printed number used to be the instantaneous level, so the same finding read as a new
        // one on every poll. Now a single deviating sample barely moves it.
        var a = AddNode("a", 1, 1);
        var b = AddNode("b", 7, 1);
        GivenSetting("a", "absorption", "3.0");

        for (var i = 0; i < 30; i++) { Hears(a, b, rssi: -57, distance: 1.0); _sut.Analyze(); }
        var settled = _sut.Analyze().SignalOutliers.Single().MeasuredRssi;

        Hears(a, b, rssi: -97, distance: 1.0);      // one wild sample, 40 dB away
        var after = _sut.Analyze().SignalOutliers.Single().MeasuredRssi;

        Assert.That(Math.Abs(after - settled), Is.LessThan(5),
            "a single outlying sample must not rewrite the finding");
    }

    [Test]
    public void SplitsFitQualityByRange()
    {
        // A single figure over all pairs hides the structure that explains most errors - and the
        // split is in dB, because the same noise is centimetres up close and metres far away.
        var a = AddNode("a", 1, 1);
        var b = AddNode("b", 3, 1);     // 2 m - near
        var c = AddNode("c", 11, 1);    // 10 m - far
        GivenSetting("a", "absorption", "3.0");
        Hears(a, b, rssi: -68, distance: 2.0);
        Hears(a, c, rssi: -89, distance: 10.0);

        var result = AnalyzeSettled();

        Assert.That(result.NearFarSplitM, Is.EqualTo(4.0));
        Assert.That(result.Near.Pairs, Is.EqualTo(1));
        Assert.That(result.Far.Pairs, Is.EqualTo(1));
        Assert.That(result.Near.MedianAbsRssiErrorDb, Is.Not.Null);
    }

    [Test]
    public void FindsNothingWorthSayingAboutAHealthyInstallation()
    {
        // The check that matters most for a first-time user: silence when there is nothing wrong.
        // A diagnostic that always finds something teaches people to ignore it.
        var nodes = AddGrid();
        foreach (var n in nodes) GivenSetting(n.Id, "absorption", "3.0");

        // Levels exactly as absorption 3.0 predicts for the mapped distance.
        foreach (var rx in nodes)
        foreach (var tx in nodes)
        {
            if (rx.Id == tx.Id) continue;
            var map = rx.Location.DistanceTo(tx.Location);
            Hears(rx, tx, rssi: -59 - 30 * Math.Log10(map), distance: map);
        }

        Assert.That(AnalyzeSettled().Issues, Is.Empty);
    }
}
