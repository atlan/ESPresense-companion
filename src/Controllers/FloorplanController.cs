using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace ESPresense.Controllers;

/// <summary>
/// Editing endpoints for the map's floorplan editor: node positions, room polygons and floor
/// bounds. Writes go through ConfigLoader.SaveSectionAsync ("nodes" / "floors" sections), so the
/// running system picks changes up via the normal config poll within ~1s.
///
/// NOTE: SaveSectionAsync re-serializes a whole section from the object model - comments and hand
/// formatting INSIDE the nodes:/floors: sections are lost on first save. A timestamped backup of
/// config.yaml is written once per app run before the first editor write as a safety net.
/// </summary>
[ApiController]
public class FloorplanController(ConfigLoader configLoader, State state) : ControllerBase
{
    private static readonly object BackupLock = new();
    private static bool _backedUp;

    private void EnsureBackup()
    {
        lock (BackupLock)
        {
            if (_backedUp) return;
            try
            {
                var backupPath = configLoader.ConfigPath + $".bak-floorplan-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                System.IO.File.Copy(configLoader.ConfigPath, backupPath);
                Log.Information("Floorplan editor: backed up config to {Path}", backupPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Floorplan editor: config backup failed (continuing anyway)");
            }
            _backedUp = true;
        }
    }

    public class NodeUpsertRequest
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        /// <summary>Floor ids; required for new nodes, existing nodes keep theirs when omitted.</summary>
        public string[]? Floors { get; set; }
    }

    [HttpPost("api/floorplan/node")]
    public async Task<IActionResult> UpsertNode([FromBody] NodeUpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id)) return BadRequest(new { error = "Node id is required" });
        var c = configLoader.Config;
        if (c == null) return StatusCode(500, new { error = "Config not loaded" });

        try
        {
            var nodes = c.Nodes.ToList();
            var existing = nodes.FirstOrDefault(n => string.Equals(n.GetId(), req.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                if (req.Floors is not { Length: > 0 })
                    return BadRequest(new { error = "New nodes need at least one floor id" });
                existing = new ConfigNode { Id = req.Id, Name = req.Name ?? req.Id, Enabled = true, Stationary = true };
                nodes.Add(existing);
            }

            existing.Point = new[] { req.X, req.Y, req.Z };
            if (req.Floors is { Length: > 0 }) existing.Floors = req.Floors;
            if (!string.IsNullOrWhiteSpace(req.Name)) existing.Name = req.Name;

            // Recompute the authoritative room assignment from the new position - same convention
            // the room field always had ("the room this node was placed in via the map editor").
            var floor = existing.Floors is { Length: > 0 }
                ? state.Floors.Values.FirstOrDefault(f => existing.Floors.Contains(f.Id, StringComparer.OrdinalIgnoreCase))
                : null;
            var room = SpatialUtils.FindRoomContaining(new Point3D(req.X, req.Y, req.Z), floor);
            existing.Room = room?.Id;

            EnsureBackup();
            c.Nodes = nodes.ToArray();
            await configLoader.SaveSectionAsync("nodes", c.Nodes);
            Log.Information("Floorplan editor: node {Id} saved at ({X}, {Y}, {Z}), room={Room}", req.Id, req.X, req.Y, req.Z, existing.Room);
            return Ok(new { saved = true, room = existing.Room });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Floorplan editor: failed to save node {Id}", req.Id);
            return StatusCode(500, new { error = "Failed to save node" });
        }
    }

    [HttpDelete("api/floorplan/node/{id}")]
    public async Task<IActionResult> DeleteNode(string id)
    {
        var c = configLoader.Config;
        if (c == null) return StatusCode(500, new { error = "Config not loaded" });

        var nodes = c.Nodes.ToList();
        var removed = nodes.RemoveAll(n => string.Equals(n.GetId(), id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return NotFound(new { error = $"Node '{id}' not found in config" });

        EnsureBackup();
        c.Nodes = nodes.ToArray();
        await configLoader.SaveSectionAsync("nodes", c.Nodes);
        Log.Information("Floorplan editor: node {Id} removed from config", id);
        return Ok(new { deleted = true });
    }

    public class RoomUpsertRequest
    {
        public string FloorId { get; set; } = "";
        /// <summary>Existing room id to update; omit to create a new room (then Name is required).</summary>
        public string? RoomId { get; set; }
        public string? Name { get; set; }
        public double[][] Points { get; set; } = Array.Empty<double[]>();
    }

    [HttpPost("api/floorplan/room")]
    public async Task<IActionResult> UpsertRoom([FromBody] RoomUpsertRequest req)
    {
        if (req.Points.Length < 3) return BadRequest(new { error = "A room needs at least 3 points" });
        var c = configLoader.Config;
        if (c == null) return StatusCode(500, new { error = "Config not loaded" });

        var floor = c.Floors.FirstOrDefault(f => string.Equals(f.GetId(), req.FloorId, StringComparison.OrdinalIgnoreCase));
        if (floor == null) return NotFound(new { error = $"Floor '{req.FloorId}' not found" });

        try
        {
            var rooms = (floor.Rooms ?? Array.Empty<ConfigRoom>()).ToList();
            ConfigRoom? room = null;
            if (!string.IsNullOrWhiteSpace(req.RoomId))
            {
                room = rooms.FirstOrDefault(r => string.Equals(r.GetId(), req.RoomId, StringComparison.OrdinalIgnoreCase));
                if (room == null) return NotFound(new { error = $"Room '{req.RoomId}' not found on floor '{req.FloorId}'" });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "New rooms need a name" });
                room = new ConfigRoom { Name = req.Name };
                rooms.Add(room);
            }

            room.Points = req.Points;
            if (!string.IsNullOrWhiteSpace(req.Name)) room.Name = req.Name;

            EnsureBackup();
            floor.Rooms = rooms.ToArray();
            await configLoader.SaveSectionAsync("floors", c.Floors);
            Log.Information("Floorplan editor: room {Room} on floor {Floor} saved ({Points} points)",
                room.GetId(), req.FloorId, req.Points.Length);
            return Ok(new { saved = true, roomId = room.GetId() });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Floorplan editor: failed to save room on floor {Floor}", req.FloorId);
            return StatusCode(500, new { error = "Failed to save room" });
        }
    }

    [HttpDelete("api/floorplan/room/{floorId}/{roomId}")]
    public async Task<IActionResult> DeleteRoom(string floorId, string roomId)
    {
        var c = configLoader.Config;
        if (c == null) return StatusCode(500, new { error = "Config not loaded" });

        var floor = c.Floors.FirstOrDefault(f => string.Equals(f.GetId(), floorId, StringComparison.OrdinalIgnoreCase));
        if (floor == null) return NotFound(new { error = $"Floor '{floorId}' not found" });

        var rooms = (floor.Rooms ?? Array.Empty<ConfigRoom>()).ToList();
        var removed = rooms.RemoveAll(r => string.Equals(r.GetId(), roomId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return NotFound(new { error = $"Room '{roomId}' not found on floor '{floorId}'" });

        EnsureBackup();
        floor.Rooms = rooms.ToArray();
        await configLoader.SaveSectionAsync("floors", c.Floors);
        Log.Information("Floorplan editor: room {Room} on floor {Floor} removed", roomId, floorId);
        return Ok(new { deleted = true });
    }

    public class FloorBoundsRequest
    {
        public string FloorId { get; set; } = "";
        public double[][] Bounds { get; set; } = Array.Empty<double[]>();
    }

    [HttpPost("api/floorplan/floor-bounds")]
    public async Task<IActionResult> SetFloorBounds([FromBody] FloorBoundsRequest req)
    {
        if (req.Bounds.Length != 2 || req.Bounds.Any(b => b.Length != 3))
            return BadRequest(new { error = "Bounds must be two [x,y,z] corner points" });
        var c = configLoader.Config;
        if (c == null) return StatusCode(500, new { error = "Config not loaded" });

        var floor = c.Floors.FirstOrDefault(f => string.Equals(f.GetId(), req.FloorId, StringComparison.OrdinalIgnoreCase));
        if (floor == null) return NotFound(new { error = $"Floor '{req.FloorId}' not found" });

        EnsureBackup();
        floor.Bounds = req.Bounds;
        await configLoader.SaveSectionAsync("floors", c.Floors);
        Log.Information("Floorplan editor: floor {Floor} bounds saved", req.FloorId);
        return Ok(new { saved = true });
    }
}
