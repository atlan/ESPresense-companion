using ESPresense.Models;
using ESPresense.Utils;

namespace ESPresense.Optimizers;

public class AbsorptionAvgOptimizer : IOptimizer
{
    private readonly State _state;

    public AbsorptionAvgOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Absorption Avg";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();

        foreach (var g in os.ByRx())
        {
            var pathLossExponents = new List<double>();
            foreach (var m in g)
            {
                if (SpatialUtils.IsCrossFloor(m.Rx, m.Tx)) continue;

                double distance = m.Rx.Location.DistanceTo(m.Tx.Location);

                // At ~1m, log10(distance) is ~0 - dividing by it blows the exponent up to
                // +-Infinity/NaN and silently corrupts the whole node's averaged absorption
                // (a single bad sample can push the average out of bounds, skipping the node
                // entirely). Skip samples too close to the 1m reference distance to divide by.
                double logDistance = Math.Log10(distance);
                if (Math.Abs(logDistance) < 0.01) continue;

                double rssiDiff = m.Rssi - m.RefRssi;
                double pathLossExponent = -rssiDiff / (10 * logDistance);

                pathLossExponents.Add(pathLossExponent);
            }
            if (pathLossExponents.Count > 0)
            {
                var absorption = pathLossExponents.Average();
                if (absorption < _state.Config?.Optimization.AbsorptionMin) continue;
                if (absorption > _state.Config?.Optimization.AbsorptionMax) continue;
                results.Nodes.Add(g.Key.Id, new ProposedValues { Absorption = absorption });
            }
        }

        return results;
    }
}