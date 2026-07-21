using System;
using System.Linq;
using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;
using ESPresense.Services;

namespace ESPresense.Locators;

public class NadarayaWatsonMultilateralizer(Device device, Floor floor, State state, NodeTelemetryStore nts) : ILocate
{
    /// <summary>
    /// The core Nadaraya-Watson weighted-centroid estimate, factored out so the wizard's locator
    /// replay can score candidate bandwidth/kernel values against walk-test ground truth using
    /// EXACTLY the math the live locator runs - a reimplementation would silently drift.
    /// </summary>
    public static (Point3D est, double weightedError) Estimate(
        IReadOnlyList<(Point3D loc, double dist)> heard, double bandwidth, string? kernel)
    {
        const double EPS = 1e-6;
        var weights = kernel == "gaussian"
            ? heard.Select(n => Math.Exp(-Math.Pow(n.dist, 2) / (2 * Math.Pow(Math.Max(bandwidth, EPS), 2)))).ToArray()
            : heard.Select(n => 1.0 / (Math.Pow(n.dist, 2) + EPS)).ToArray();
        var wSum = weights.Sum();
        if (wSum < EPS) weights = heard.Select(n => 1.0 / (Math.Pow(n.dist, 2) + EPS)).ToArray();
        wSum = weights.Sum();

        var est = new Point3D(
            heard.Zip(weights, (n, w) => n.loc.X * w).Sum() / wSum,
            heard.Zip(weights, (n, w) => n.loc.Y * w).Sum() / wSum,
            heard.Zip(weights, (n, w) => n.loc.Z * w).Sum() / wSum
        );

        var weightedError = heard.Zip(weights, (n, w) =>
        {
            double diff = est.DistanceTo(n.loc) - n.dist;
            return w * diff * diff;
        }).Sum() / wSum;

        return (est, weightedError);
    }

    public bool Locate(Scenario scenario)
    {
        var heard = device.Nodes.Values
            .Where(n => n.Current && (n.Node?.Floors?.Contains(floor) ?? false))
            .OrderBy(n => n.Distance)
            .ToArray();

        if (heard.Length <= 1)
        {
            scenario.Confidence = 0;
            scenario.Room = null;
            scenario.Error = null;
            return false;
        }

        scenario.Minimum = heard.Min(n => (double?)n.Distance);
        scenario.LastHit = heard.Max(n => n.LastHit);
        scenario.Fixes = heard.Length;
        scenario.Floor = floor;

        Point3D est;
        double weightedError = 0;

        try
        {
            if (heard.Length < 3 || floor.Bounds == null)
            {
                est = Point3D.MidPoint(heard[0].Node!.Location, heard[1].Node!.Location);
                // Only 2 nodes heard - not enough for a real weighted fit, but leaving Error null
                // zeroes the entire "quality" half of CalculateConfidence, which structurally favors
                // any floor with >=3 audible nodes regardless of whether it's the correct floor
                // (floors with fewer total nodes, e.g. a small basement, then can basically never win
                // against a densely-covered floor). Compute a genuine residual instead: how far the
                // midpoint's geometric distance to each node differs from that node's measured
                // distance. Not a fit (no free parameters, so it can't be gamed), just a real signal.
                scenario.Error = heard.Average(n => Math.Pow(est.DistanceTo(n.Node!.Location) - n.Distance, 2));
                scenario.PearsonCorrelation = null;
            }
            else
            {
                var nwConfig = state.Config?.Locators?.NadarayaWatson;
                (est, weightedError) = Estimate(
                    heard.Select(n => (n.Node!.Location, n.Distance)).ToList(),
                    nwConfig?.Bandwidth ?? 0.5,
                    nwConfig?.Kernel);
                scenario.Error = weightedError;
            }

            scenario.UpdateLocation(est);

            var measured = heard.Select(n => n.Distance).ToList();
            var calculated = heard.Select(n => est.DistanceTo(n.Node!.Location)).ToList();
            scenario.PearsonCorrelation = MathUtils.CalculatePearsonCorrelation(measured, calculated);

            // Get count of possible online nodes for this floor
            int nodesPossibleOnline = state.Nodes.Values
                .Count(n =>
                    (n.Floors?.Contains(floor) ?? false) &&
                    (nts.Online(n.Id)));

            // Use the centralized confidence calculation
            scenario.Confidence = MathUtils.CalculateConfidence(
                scenario.Error,
                scenario.PearsonCorrelation,
                heard.Length,
                nodesPossibleOnline
            );
        }
        catch (Exception ex)
        {
            scenario.UpdateLocation(scenario.Location); // revert to last good
            scenario.Confidence = 0;
            scenario.Error = null;
            Log.Error("Locator error for {Device}: {Message}", device, ex.Message);
        }

        if (scenario.Confidence <= 0) return false;
        if (scenario.Location.DistanceTo(scenario.LastLocation) < 0.1) return false;

        // Falls back to the nearest room within a small tolerance if the (still noisy)
        // weighted-centroid point misses every polygon - helps small rooms disproportionately,
        // since ordinary RSSI noise more easily pushes the point past a tight boundary. Also
        // resists flicker between two rooms that share a wall (no gap between polygons, so the
        // point is always inside SOME room there - the tolerance fallback above can't help).
        scenario.Room = SpatialUtils.FindRoomWithHysteresis(scenario.Location, floor, scenario.Room);

        // If the single closest heard node is right on top of the device, trust which room
        // *that node* belongs to over the polygon result: in a small room a strongly dominant
        // nearest node is a far more reliable signal than the weighted-centroid position, which
        // can still land in a neighboring (often larger) room's polygon even while sitting almost
        // on top of the small room's own node. Prefer the explicit "room" field the Companion UI
        // writes when a node is placed on the map (previously never read anywhere in the
        // backend); fall back to matching the node's id/name against a room name (works when a
        // node happens to be named after its room, e.g. "Toilette" node in "Toilette" room, but
        // silently misses cases like a "bath_room"/"Bath Node" node in room "Badezimmer").
        const double DominantNodeDistance = 1.0;
        if (heard[0].Distance < DominantNodeDistance)
        {
            var dominantNode = heard[0].Node!;
            Room? overrideRoom = dominantNode.RoomId != null && floor.Rooms.TryGetValue(dominantNode.RoomId, out var configuredRoom) ? configuredRoom : null;
            overrideRoom ??= SpatialUtils.FindRoomByNodeName(floor, dominantNode.Id, dominantNode.Name);
            if (overrideRoom != null) scenario.Room = overrideRoom;
        }

        return true;
    }
}
