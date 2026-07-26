using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Services;

/// <summary>
/// Decides where the next walk test point should go.
///
/// Replaces the original rule - the midpoint of the node pair with the worst calibration error -
/// which was measured against on 2026-07-26 and failed on four counts:
///
/// 1. It averaged the node COORDINATES including height, so the suggested height was an artefact of
///    where two boxes happen to be screwed to a wall. On this installation the ground floor's nodes
///    sit between +0.30 m and +2.20 m above the floor, so pair midpoints land anywhere in that band -
///    sometimes near the skirting board, sometimes above head height, never deliberately where a
///    device is actually carried. The walk points that were recorded by hand sit at 0.9-1.1 m.
/// 2. It optimised for the node-to-node calibration, while the measured driver of position error is
///    the distance to the NEAREST node (within 1.5 m: 1.11 m median error, beyond: 2.47 m; the
///    correlation with the third-nearest node was r = -0.01).
/// 3. Rooms never entered into it, although the room hit rate - 61 % here - is what presence
///    automations actually consume, and a midpoint between two nodes can land in a wall.
/// 4. It ignored floor detection, which the benchmark now scores and which is this installation's
///    weakest link (basement 77 % against ground 98 %).
///
/// It also clustered: all three live suggestions sat in one corner around the same three nodes.
///
/// The rule here instead: rooms that have never been measured come first, because accuracy somewhere
/// nobody has stood is not unknown-but-probably-fine, it is simply unknown. Within that, floors whose
/// floor detection is weakest, then rooms whose node coverage is worst.
/// </summary>
public class WalkPointPlanner(State state, WalkTestService walkTest, CalibrationBenchmark benchmark)
{
    /// <summary>Grid spacing when sampling a room, matching <see cref="WizardDiagnostics"/>.</summary>
    private const double SampleStepM = 0.5;

    /// <summary>Within this distance to a node a spot measured 1.11 m median error here; beyond it, 2.47 m.</summary>
    private const double GoodCoverageM = 1.5;

    /// <summary>A locator needs three ranges, so a suggested spot that fewer nodes can plausibly hear is useless.</summary>
    private const int MinNodesInRange = 3;

    /// <summary>Beyond this a node contributes nothing usable, so it does not count towards the three.</summary>
    private const double UsableRangeM = 12.0;

    /// <summary>Height to place the device at when no walk point on that floor says otherwise.</summary>
    private const double DefaultDeviceHeightM = 1.0;

    public List<WalkPointSuggestion> Suggest(int count = 3)
    {
        var floorHitRates = (benchmark.Last?.Floors ?? new List<BenchmarkFloor>())
            .Where(f => f.FloorHitRate.HasValue)
            .ToDictionary(f => f.FloorId, f => f.FloorHitRate!.Value, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<WalkPointSuggestion>();

        foreach (var floor in state.Floors.Values)
        {
            if (floor.Id == null || floor.Rooms.IsEmpty) continue;

            var nodes = state.Nodes.Values
                .Where(n => n.HasLocation && (n.Floors?.Any(f => f.Id == floor.Id) ?? false))
                .Select(n => n.Location)
                .ToList();
            if (nodes.Count == 0) continue;

            var points = walkTest.GetPoints()
                .Where(p => string.Equals(p.FloorId, floor.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Measured beats guessed: put the device where it was actually carried on this floor.
            var heights = points.Select(p => p.Z).OrderBy(z => z).ToList();
            var deviceZ = heights.Count > 0 ? heights[heights.Count / 2] : DefaultDeviceHeightM;

            floorHitRates.TryGetValue(floor.Id, out var hitRate);
            var floorPenalty = floorHitRates.ContainsKey(floor.Id) ? 1.0 - hitRate : 0.0;

            foreach (var room in floor.Rooms.Values)
            {
                if (room.Polygon == null) continue;
                var vertices = room.Polygon.Vertices.ToList();
                if (vertices.Count < 3) continue;

                var alreadyMeasured = points.Count(p => room.Polygon.EnclosesPoint(new Point2D(p.X, p.Y)));

                var minX = vertices.Min(v => v.X); var maxX = vertices.Max(v => v.X);
                var minY = vertices.Min(v => v.Y); var maxY = vertices.Max(v => v.Y);

                Point3D? worst = null;
                var worstNearest = -1.0;
                var nearestDistances = new List<double>();

                for (var x = minX; x <= maxX; x += SampleStepM)
                for (var y = minY; y <= maxY; y += SampleStepM)
                {
                    if (!room.Polygon.EnclosesPoint(new Point2D(x, y))) continue;
                    var candidate = new Point3D(x, y, deviceZ);
                    var distances = nodes.Select(n => n.DistanceTo(candidate)).OrderBy(d => d).ToList();
                    nearestDistances.Add(distances[0]);

                    // The worst-covered spot is where the room's accuracy is decided - but only if
                    // enough nodes can still hear it, otherwise the measurement yields no fix at all.
                    if (distances.Count(d => d <= UsableRangeM) < MinNodesInRange) continue;
                    if (distances[0] <= worstNearest) continue;
                    worstNearest = distances[0];
                    worst = candidate;
                }

                if (worst is not { } target || nearestDistances.Count == 0) continue;

                nearestDistances.Sort();
                var medianNearest = nearestDistances[nearestDistances.Count / 2];

                // Never measured dominates everything else: a room nobody has stood in has no
                // accuracy figure at all, and no amount of poor coverage elsewhere is worth more
                // than turning an unknown into a number.
                var score = (alreadyMeasured == 0 ? 100.0 : 0.0)
                            + floorPenalty * 50.0
                            + Math.Min(medianNearest / GoodCoverageM, 3.0) * 10.0;

                candidates.Add(new WalkPointSuggestion
                {
                    X = Math.Round(target.X, 2),
                    Y = Math.Round(target.Y, 2),
                    Z = Math.Round(target.Z, 2),
                    FloorId = floor.Id,
                    FloorName = floor.Name,
                    RoomId = room.Id,
                    RoomName = room.Name,
                    NearestNodeM = Math.Round(worstNearest, 2),
                    MedianNearestNodeM = Math.Round(medianNearest, 2),
                    ExistingPoints = alreadyMeasured,
                    FloorHitRate = floorHitRates.ContainsKey(floor.Id) ? Math.Round(hitRate, 3) : null,
                    Score = Math.Round(score, 1),
                    Reason = Explain(room, alreadyMeasured, worstNearest, floor, floorHitRates.ContainsKey(floor.Id) ? hitRate : null)
                });
            }
        }

        // Spread before depth: one round of the best room per floor, then fill by score. Without it
        // the whole list lands on the worst floor, and a walk test that only covers one storey cannot
        // tell a floor-detection problem from a coverage problem.
        var ordered = new List<WalkPointSuggestion>();
        var byFloor = candidates.GroupBy(c => c.FloorId, StringComparer.OrdinalIgnoreCase)
                                .OrderByDescending(g => g.Max(c => c.Score));
        foreach (var group in byFloor)
        {
            var best = group.OrderByDescending(c => c.Score).First();
            ordered.Add(best);
        }
        ordered.AddRange(candidates.Except(ordered).OrderByDescending(c => c.Score));

        return ordered.Take(count).ToList();
    }

    private static string Explain(Room room, int existing, double nearest, Floor floor, double? hitRate)
    {
        var name = room.Name ?? room.Id;
        var parts = new List<string>();

        parts.Add(existing == 0
            ? $"'{name}' has never been measured - there is no accuracy figure for it at all"
            : $"'{name}' has {existing} point{(existing == 1 ? "" : "s")} already, this spot covers the part they miss");

        parts.Add(nearest > GoodCoverageM
            ? $"the nearest node here is {nearest:0.0} m away, so expect around 2.5 m error until one is added"
            : $"the nearest node is {nearest:0.0} m away, which is the range that measured 1.1 m error");

        if (hitRate is { } r && r < 0.95)
            parts.Add($"and floor detection on '{floor.Name ?? floor.Id}' only gets the storey right {r:P0} of the time");

        return string.Join(", ", parts) + ".";
    }
}

public class WalkPointSuggestion
{
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Height a device is actually carried at - the median of this floor's walk points, never node height.</summary>
    public double Z { get; set; }
    public string? FloorId { get; set; }
    public string? FloorName { get; set; }
    public string? RoomId { get; set; }
    public string? RoomName { get; set; }
    /// <summary>Distance to the nearest node at the suggested spot.</summary>
    public double NearestNodeM { get; set; }
    /// <summary>Distance to the nearest node across the room as a whole.</summary>
    public double MedianNearestNodeM { get; set; }
    public int ExistingPoints { get; set; }
    public double? FloorHitRate { get; set; }
    public double Score { get; set; }
    public string Reason { get; set; } = "";
}
