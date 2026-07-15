using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Utils;

/// <summary>
/// Utilities for spatial lookups and geometric operations
/// </summary>
public static class SpatialUtils
{
    /// <summary>
    /// Finds the floor that contains the given 3D location based on floor bounds
    /// </summary>
    public static Floor? FindFloorContaining(Point3D location, IEnumerable<Floor> floors)
    {
        foreach (var floor in floors)
        {
            if (floor.Bounds is not { Length: >= 2 })
                continue;

            var min = floor.Bounds[0];
            var max = floor.Bounds[1];

            if (location.X >= min.X && location.X <= max.X &&
                location.Y >= min.Y && location.Y <= max.Y &&
                location.Z >= min.Z && location.Z <= max.Z)
            {
                return floor;
            }
        }

        return null;
    }

    // If a location isn't strictly inside any room polygon, treat it as inside the nearest
    // one anyway when within this distance of its boundary. Small rooms (a toilet, a closet)
    // have so little margin that ordinary RSSI/multilateration noise routinely pushes the
    // computed point just past the edge into a neighboring room's (often larger) polygon -
    // this recovers those near-miss cases without needing a real geometric polygon inflation.
    const double RoomBoundaryTolerance = 0.5;

    /// <summary>
    /// Finds the room within a floor that contains the given location, falling back to the
    /// nearest room if the point is just outside all polygons within <see cref="RoomBoundaryTolerance"/>.
    /// </summary>
    public static Room? FindRoomContaining(Point3D location, Floor? floor)
    {
        if (floor == null)
            return null;

        var point = location.ToPoint2D();

        var direct = floor.Rooms.Values.FirstOrDefault(room => room.Polygon?.EnclosesPoint(point) ?? false);
        if (direct != null) return direct;

        Room? nearest = null;
        double nearestDist = double.MaxValue;
        foreach (var room in floor.Rooms.Values)
        {
            if (room.Polygon == null) continue;
            var d = DistanceToPolygonBoundary(point, room.Polygon);
            if (d < nearestDist)
            {
                nearestDist = d;
                nearest = room;
            }
        }

        return nearestDist <= RoomBoundaryTolerance ? nearest : null;
    }

    /// <summary>
    /// Finds a room on the floor whose name matches the given node's id or name (nodes are
    /// often named after the room they're placed in, e.g. a "Toilette" node in the "Toilette"
    /// room) - a useful room-assignment signal for small rooms where multilateration alone
    /// isn't precise enough to reliably land inside a tight polygon.
    /// </summary>
    public static Room? FindRoomByNodeName(Floor? floor, string? nodeId, string? nodeName)
    {
        if (floor == null) return null;
        return floor.Rooms.Values.FirstOrDefault(r =>
            (nodeId != null && string.Equals(r.Name, nodeId, StringComparison.OrdinalIgnoreCase)) ||
            (nodeName != null && string.Equals(r.Name, nodeName, StringComparison.OrdinalIgnoreCase)));
    }

    static double DistanceToPolygonBoundary(Point2D point, Polygon2D polygon)
    {
        var vertices = polygon.Vertices.ToList();
        if (vertices.Count < 2) return double.MaxValue;

        double min = double.MaxValue;
        for (int i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            min = Math.Min(min, DistanceToSegment(point, a, b));
        }
        return min;
    }

    static double DistanceToSegment(Point2D p, Point2D a, Point2D b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var apx = p.X - a.X;
        var apy = p.Y - a.Y;
        var lenSq = abx * abx + aby * aby;
        var t = lenSq > 1e-12 ? Math.Clamp((apx * abx + apy * aby) / lenSq, 0.0, 1.0) : 0.0;
        var cx = a.X + t * abx;
        var cy = a.Y + t * aby;
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Finds both floor and room containing the given location
    /// </summary>
    public static (Floor? floor, Room? room) FindFloorAndRoom(Point3D location, IEnumerable<Floor> floors)
    {
        var floor = FindFloorContaining(location, floors);
        var room = FindRoomContaining(location, floor);
        return (floor, room);
    }

    /// <summary>
    /// True if the two nodes have no floor in common. Live per-floor localization only ever uses
    /// same-floor nodes, so a calibration pair spanning two floors (e.g. separated by a concrete
    /// slab) carries no useful signal for fitting a node's RF calibration - a single shared
    /// absorption/adjustment value can't fit same-floor and cross-floor attenuation at once
    /// (verified live: fixing one made the other 2-4x worse).
    /// </summary>
    public static bool IsCrossFloor(OptNode rx, OptNode tx)
    {
        if (rx.FloorIds is not { Length: > 0 } rxFloors || tx.FloorIds is not { Length: > 0 } txFloors)
            return false;
        return !rxFloors.Intersect(txFloors, StringComparer.OrdinalIgnoreCase).Any();
    }
}
