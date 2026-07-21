using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ESPresense.Companion.Tests.Controllers;

public class FloorplanControllerTests
{
    private State _state = null!;
    private string _configDir = null!;
    private ConfigLoader _configLoader = null!;
    private FloorplanController _sut = null!;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        await File.WriteAllTextAsync(Path.Combine(_configDir, "config.yaml"), @"mqtt:
  host: localhost

floors:
- id: ground
  name: Ground
  bounds:
  - - 0
    - 0
    - 0
  - - 10
    - 10
    - 3
  rooms:
  - id: kitchen
    name: Kitchen
    points:
    - - 0
      - 0
    - - 5
      - 0
    - - 5
      - 5
    - - 0
      - 5

nodes:
- id: existing
  name: Existing
  point:
  - 1
  - 1
  - 0.5
  floors:
  - ground
");
        _configLoader = new ConfigLoader(_configDir);
        await _configLoader.ConfigAsync(); // ctor loads asynchronously - wait before touching Config
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        _sut = new FloorplanController(_configLoader, _state);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, true);
    }

    private Task<string> ConfigText() => File.ReadAllTextAsync(Path.Combine(_configDir, "config.yaml"));

    [Test]
    public async Task UpsertNode_NewNode_RequiresFloors()
    {
        var result = await _sut.UpsertNode(new FloorplanController.NodeUpsertRequest { Id = "new_node", X = 2, Y = 2, Z = 1 });
        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpsertNode_NewNode_WritesConfig()
    {
        var result = await _sut.UpsertNode(new FloorplanController.NodeUpsertRequest
        {
            Id = "new_node", Name = "New Node", X = 2, Y = 2, Z = 1, Floors = new[] { "ground" }
        });

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        var text = await ConfigText();
        Assert.That(text, Does.Contain("new_node"));
        Assert.That(text, Does.Contain("existing"), "existing nodes must survive the section rewrite");
    }

    [Test]
    public async Task UpsertNode_ExistingNode_UpdatesPositionKeepsFloors()
    {
        var result = await _sut.UpsertNode(new FloorplanController.NodeUpsertRequest { Id = "existing", X = 3, Y = 4, Z = 1 });

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        var text = await ConfigText();
        Assert.That(text, Does.Contain("- 3"));
        Assert.That(text, Does.Contain("- 4"));
        Assert.That(text, Does.Contain("ground"), "floors list untouched when omitted");
    }

    [Test]
    public async Task DeleteNode_RemovesFromConfig()
    {
        var result = await _sut.DeleteNode("existing");

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        var text = await ConfigText();
        Assert.That(text, Does.Not.Contain("- id: existing"));
    }

    [Test]
    public async Task UpsertRoom_RejectsFewerThanThreePoints()
    {
        var result = await _sut.UpsertRoom(new FloorplanController.RoomUpsertRequest
        {
            FloorId = "ground", RoomId = "kitchen", Points = new[] { new[] { 0.0, 0 }, new[] { 1.0, 1 } }
        });
        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpsertRoom_NewRoom_RequiresName()
    {
        var result = await _sut.UpsertRoom(new FloorplanController.RoomUpsertRequest
        {
            FloorId = "ground", Points = new[] { new[] { 0.0, 0 }, new[] { 1.0, 0 }, new[] { 1.0, 1 } }
        });
        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DeleteFloor_BlockedWhileNodesReferenceIt()
    {
        var result = await _sut.DeleteFloor("ground");

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>(), "a floor with nodes must not be deletable");
        var text = await ConfigText();
        Assert.That(text, Does.Contain("- id: ground"));
    }

    [Test]
    public async Task DeleteFloor_SucceedsAfterNodesRemoved()
    {
        await _sut.DeleteNode("existing");
        var result = await _sut.DeleteFloor("ground");

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        var text = await ConfigText();
        Assert.That(text, Does.Not.Contain("- id: ground"));
    }

    [Test]
    public async Task SetFloorBounds_ValidatesShape()
    {
        var bad = await _sut.SetFloorBounds(new FloorplanController.FloorBoundsRequest
        {
            FloorId = "ground", Bounds = new[] { new[] { 0.0, 0 } }
        });
        Assert.That(bad, Is.TypeOf<BadRequestObjectResult>());

        var ok = await _sut.SetFloorBounds(new FloorplanController.FloorBoundsRequest
        {
            FloorId = "ground", Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 12.0, 8, 3 } }
        });
        Assert.That(ok, Is.TypeOf<OkObjectResult>());
        Assert.That(await ConfigText(), Does.Contain("- 12"));
    }
}
