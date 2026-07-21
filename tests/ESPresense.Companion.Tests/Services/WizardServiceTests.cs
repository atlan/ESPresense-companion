using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class WizardServiceTests
{
    private State _state = null!;
    private string _configDir = null!;
    private ConfigLoader _configLoader = null!;
    private WizardService _sut = null!;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        // bounds Z deliberately swapped on 'broken' floor (second Z smaller - the historical
        // ceiling-height-instead-of-absolute-Z data entry mistake); tiny + overlapping rooms.
        await File.WriteAllTextAsync(Path.Combine(_configDir, "config.yaml"), @"mqtt:
  host: localhost

floors:
- id: ok
  name: OkFloor
  bounds:
  - - 0
    - 0
    - 0
  - - 10
    - 10
    - 3
  rooms:
  - id: roomy
    name: Roomy
    points:
    - - 0
      - 0
    - - 5
      - 0
    - - 5
      - 5
    - - 0
      - 5
  - id: tiny
    name: Tiny
    points:
    - - 6
      - 6
    - - 6.5
      - 6
    - - 6.5
      - 6.5
    - - 6
      - 6.5
  - id: overlapper
    name: Overlapper
    points:
    - - 2
      - 2
    - - 8
      - 2
    - - 8
      - 4
    - - 2
      - 4
- id: broken
  name: BrokenFloor
  bounds:
  - - 0
    - 0
    - 5
  - - 10
    - 10
    - 2.4

nodes:
- id: outside
  name: Outside
  point:
  - 15
  - 15
  - 1
  floors:
  - ok
");
        _configLoader = new ConfigLoader(_configDir);
        await _configLoader.ConfigAsync(); // ctor loads asynchronously - wait before touching Config
        var nts = new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object);
        _state = new State(_configLoader, nts);
        _sut = new WizardService(_state, nts, _configLoader, new PairErrorTracker(_state));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, true);
    }

    [Test]
    public void Validate_FlagsSwappedBounds()
    {
        var result = _sut.Validate();
        Assert.That(result.Issues.Any(i => i.Category == "bounds_swapped" && i.FloorId == "broken"), Is.True,
            "raw config bounds with min > max on an axis must be reported even though Floor.Update silently fixes them");
    }

    [Test]
    public void Validate_FlagsTooSmallRoom()
    {
        var result = _sut.Validate();
        Assert.That(result.Issues.Any(i => i.Category == "room_too_small" && i.RoomId == "tiny"), Is.True);
    }

    [Test]
    public void Validate_FlagsOverlappingRooms()
    {
        var result = _sut.Validate();
        Assert.That(result.Issues.Any(i => i.Category == "room_overlap"), Is.True,
            "Overlapper cuts through Roomy well beyond a shared edge");
    }

    [Test]
    public void Validate_FlagsNodeOutsideFloorBounds()
    {
        // Inject node+floor directly (the loader's ConfigChanged fired before State subscribed,
        // so config nodes aren't in State here - same manual pattern as the anchor tests).
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor { Id = "ok", Name = "OkFloor", Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 } } });
        var node = new Node("outside", NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = "outside", Name = "Outside", Point = new[] { 15.0, 15, 1 } }, new[] { floor });
        _state.Nodes["outside"] = node;

        var result = _sut.Validate();
        Assert.That(result.Issues.Any(i => i.Category == "node_outside_bounds" && i.NodeId == "outside"), Is.True);
    }
}
