using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Services;

/// <summary>
/// Backend for the calibration setup wizard: static geometry validation (bounds/polygons),
/// RSSI-vs-map placement sanity checks, and the node health gate. Read-only over State -
/// heuristic checks surface issues for the user, they never block config loading.
/// </summary>
public class WizardService(State state, NodeTelemetryStore nts, ConfigLoader configLoader)
{
    /// <summary>Rooms smaller than this (m^2) are flagged - multilateration noise (~0.5-2m) makes them hard to hit.</summary>
    private const double MinRoomAreaM2 = 1.0;

    /// <summary>A vertex of one room this far inside another room's polygon counts as an overlap (not just a shared edge).</summary>
    private const double OverlapMarginM = 0.1;

    /// <summary>Median |percent| distance error above which a node's placement is flagged as suspicious.</summary>
    private const double PlacementErrorThreshold = 0.5;

    /// <summary>Online nodes whose last telemetry is older than this are considered stuck/stale.</summary>
    private static readonly TimeSpan TelemetryStaleAfter = TimeSpan.FromSeconds(150);

    public WizardValidationResult Validate()
    {
        var result = new WizardValidationResult();
        var config = configLoader.Config;
        ValidateFloorGeometry(config, result);
        ValidateNodePositions(result);
        CheckNodePlacementSanity(result);
        return result;
    }

    private static void ValidateFloorGeometry(Config? config, WizardValidationResult result)
    {
        foreach (var cf in config?.Floors ?? Enumerable.Empty<ConfigFloor>())
        {
            var floorId = cf.GetId();

            if (cf.Bounds is not { Length: >= 2 })
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Category = "bounds_missing",
                    FloorId = floorId,
                    Message = $"Floor '{cf.Name ?? floorId}' has no usable bounds (needs two [x,y,z] corner points)."
                });
                continue;
            }

            // Floor.Update() silently swap-fixes min/max per axis; detect it here on the RAW config
            // values so the user learns their yaml has an entry mistake (e.g. Z entered as ceiling
            // HEIGHT instead of absolute Z - the exact historical MLE Math.Clamp crash cause).
            var a = cf.Bounds[0].EnsureLength3();
            var b = cf.Bounds[1].EnsureLength3();
            for (var axis = 0; axis < 3; axis++)
            {
                if (a[axis] > b[axis])
                {
                    var axisName = axis switch { 0 => "X", 1 => "Y", _ => "Z" };
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Category = "bounds_swapped",
                        FloorId = floorId,
                        Message = $"Floor '{cf.Name ?? floorId}': bounds {axisName} is larger in the first corner ({a[axis]}) than the second ({b[axis]}). " +
                                  "The backend silently swaps these, but this usually indicates a data-entry mistake (e.g. ceiling height entered instead of an absolute coordinate)."
                    });
                }
            }

            ValidateRooms(cf, floorId, result);
        }
    }

    private static void ValidateRooms(ConfigFloor cf, string floorId, WizardValidationResult result)
    {
        var polys = new List<(string roomId, string name, List<Point2D> pts)>();
        foreach (var room in cf.Rooms ?? Enumerable.Empty<ConfigRoom>())
        {
            var roomId = room.GetId();
            var pts = (room.Points ?? Array.Empty<double[]>())
                .Where(p => p.Length >= 2)
                .Select(p => new Point2D(p[0], p[1]))
                .ToList();
            // Drop a duplicated closing point (first == last) so vertex/area math isn't skewed.
            if (pts.Count >= 2 && pts[0].Equals(pts[^1], 1e-9)) pts.RemoveAt(pts.Count - 1);

            if (pts.Count < 3)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    Category = "room_degenerate",
                    FloorId = floorId,
                    RoomId = roomId,
                    Message = $"Room '{room.Name ?? roomId}' has fewer than 3 distinct polygon points."
                });
                continue;
            }

            var area = Math.Abs(ShoelaceArea(pts));
            if (area < MinRoomAreaM2)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Category = "room_too_small",
                    FloorId = floorId,
                    RoomId = roomId,
                    Message = $"Room '{room.Name ?? roomId}' is only {area:0.0} m2 - typical multilateration noise (0.5-2m) will often place devices outside such a small polygon."
                });
            }

            polys.Add((roomId, room.Name ?? roomId, pts));
        }

        for (var i = 0; i < polys.Count; i++)
        for (var j = i + 1; j < polys.Count; j++)
        {
            if (PolygonsOverlap(polys[i].pts, polys[j].pts))
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Category = "room_overlap",
                    FloorId = floorId,
                    RoomId = polys[i].roomId,
                    Message = $"Rooms '{polys[i].name}' and '{polys[j].name}' overlap by more than a shared edge - room assignment will be ambiguous in the overlapping area."
                });
            }
        }
    }

    private void ValidateNodePositions(WizardValidationResult result)
    {
        foreach (var (id, node) in state.Nodes)
        {
            if (!node.HasLocation || node.Floors is not { Length: > 0 }) continue;
            foreach (var floor in node.Floors)
            {
                if (floor.Bounds is not { Length: >= 2 }) continue;
                var min = floor.Bounds[0];
                var max = floor.Bounds[1];
                var loc = node.Location;
                if (loc.X < min.X || loc.X > max.X || loc.Y < min.Y || loc.Y > max.Y || loc.Z < min.Z || loc.Z > max.Z)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        Category = "node_outside_bounds",
                        NodeId = id,
                        FloorId = floor.Id,
                        Message = $"Node '{node.Name ?? id}' at ({loc.X:0.0}, {loc.Y:0.0}, {loc.Z:0.0}) lies outside floor '{floor.Name ?? floor.Id}' bounds " +
                                  $"({min.X:0.0}-{max.X:0.0}, {min.Y:0.0}-{max.Y:0.0}, {min.Z:0.0}-{max.Z:0.0})."
                    });
                }
            }
        }
    }

    /// <summary>
    /// Compares each node's live RSSI-estimated distances against its coordinate-implied distances
    /// to same-floor neighbors. A node whose MEDIAN error is large probably has a wrong position
    /// entered (the classic wrong-Z / transposed-coordinates case); a single bad pair is more
    /// likely an RF obstruction and stays quiet here (that's what excluded_pairs is for).
    /// </summary>
    private void CheckNodePlacementSanity(WizardValidationResult result)
    {
        foreach (var (id, node) in state.Nodes)
        {
            if (!node.HasLocation) continue;

            var errors = new List<double>();
            foreach (var rx in node.RxNodes.Values)
            {
                if (!rx.Current || rx.Rx is not { HasLocation: true } rxNode) continue;
                if (!SameFloor(node, rxNode)) continue;
                var mapDist = rx.MapDistance;
                if (mapDist <= 0 || rx.Distance <= 0) continue;
                errors.Add(Math.Abs((rx.Distance - mapDist) / mapDist));
            }

            if (errors.Count < 2) continue;

            errors.Sort();
            var median = errors[errors.Count / 2];
            if (median > PlacementErrorThreshold)
            {
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Category = "placement_mismatch",
                    NodeId = id,
                    Message = $"Node '{node.Name ?? id}': RSSI-estimated distances to {errors.Count} same-floor neighbors disagree with its map position " +
                              $"(median error {median:P0}). Check the entered X/Y/Z - a wrong height or transposed coordinates typically looks exactly like this."
                });
            }
        }
    }

    public HealthGateResult HealthGate()
    {
        var result = new HealthGateResult();
        var now = DateTime.UtcNow;

        foreach (var (id, node) in state.Nodes.OrderBy(kv => kv.Value.Name ?? kv.Key))
        {
            if (node.SourceType != NodeSourceType.Config) continue;

            var online = nts.Online(id);
            var tele = nts.Get(id);
            var lastAt = nts.LastReceivedAt(id);
            var age = lastAt.HasValue ? (now - lastAt.Value).TotalSeconds : (double?)null;
            var stale = online && (age is null || age > TelemetryStaleAfter.TotalSeconds);

            var n = new HealthGateNode
            {
                Id = id,
                Name = node.Name,
                Online = online,
                Version = tele?.Version,
                TelemetryAgeSecs = age,
                Stale = stale
            };
            result.Nodes.Add(n);

            if (!online) result.OfflineNodes.Add(id);
            if (stale) result.StaleNodes.Add(id);
        }

        result.FirmwareVersions = result.Nodes
            .Where(n => n.Online && !string.IsNullOrEmpty(n.Version))
            .Select(n => n.Version!)
            .Distinct()
            .OrderBy(v => v)
            .ToList();

        result.Passed = result.OfflineNodes.Count == 0 && result.StaleNodes.Count == 0 && result.FirmwareVersions.Count <= 1;
        return result;
    }

    private static bool SameFloor(Node a, Node b)
    {
        if (a.Floors is not { Length: > 0 } af || b.Floors is not { Length: > 0 } bf) return false;
        return af.Select(f => f.Id).Intersect(bf.Select(f => f.Id), StringComparer.OrdinalIgnoreCase).Any();
    }

    private static double ShoelaceArea(IReadOnlyList<Point2D> pts)
    {
        double sum = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var p1 = pts[i];
            var p2 = pts[(i + 1) % pts.Count];
            sum += p1.X * p2.Y - p2.X * p1.Y;
        }
        return sum / 2.0;
    }

    /// <summary>
    /// Overlap test with shared-edge tolerance: probes each polygon's vertices, edge midpoints and
    /// centroid against the other polygon, requiring the probe point to be MORE than OverlapMarginM
    /// inside (distance to the boundary) - adjacent rooms sharing an edge therefore don't trigger.
    /// </summary>
    private static bool PolygonsOverlap(List<Point2D> a, List<Point2D> b)
    {
        var polyA = new Polygon2D(a);
        var polyB = new Polygon2D(b);
        return ProbePoints(a).Any(p => StrictlyInside(p, polyB)) ||
               ProbePoints(b).Any(p => StrictlyInside(p, polyA));
    }

    private static IEnumerable<Point2D> ProbePoints(List<Point2D> pts)
    {
        double cx = 0, cy = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            yield return pts[i];
            var next = pts[(i + 1) % pts.Count];
            yield return new Point2D((pts[i].X + next.X) / 2, (pts[i].Y + next.Y) / 2);
            cx += pts[i].X;
            cy += pts[i].Y;
        }
        yield return new Point2D(cx / pts.Count, cy / pts.Count);
    }

    private static bool StrictlyInside(Point2D p, Polygon2D poly)
    {
        return poly.EnclosesPoint(p) && SpatialUtils.DistanceToPolygonBoundary(p, poly) > OverlapMarginM;
    }
}

internal static class WizardExtensions
{
    public static double[] EnsureLength3(this double[] arr)
    {
        if (arr.Length >= 3) return arr;
        var result = new double[3];
        Array.Copy(arr, result, arr.Length);
        return result;
    }
}
