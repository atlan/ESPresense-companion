using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using ESPresense.Extensions;

namespace ESPresense.Optimizers;

public class PerNodeAbsorptionRxTx : IOptimizer
{
    private readonly State _state;

    public PerNodeAbsorptionRxTx(State state)
    {
        _state = state;
    }

    public string Name => "Per Node Absorption Rx Tx Adj";

    /// <summary>
    /// When set, overrides config weights.absorption_penalty for this instance - lets the
    /// auto-tune wizard fit candidate penalty values against the same data without mutating
    /// (or racing on) the live shared config.
    /// </summary>
    public double? AbsorptionPenaltyOverride { get; init; }

    /// <summary>Auto-tune overrides for limits.absorption_min/max - same rationale as the penalty override.</summary>
    public double? AbsorptionMinOverride { get; init; }
    public double? AbsorptionMaxOverride { get; init; }

    /// <summary>Residual to minimize: "distance" (original) or "db". Overrides the config for one run.</summary>
    public string? ObjectiveOverride { get; init; }

    /// <summary>Where the regularization pulls to. Overrides both config and the fleet-median seed.</summary>
    public double? AbsorptionTargetOverride { get; init; }

    /// <summary>Huber transition in dB, for the "db" objective.</summary>
    public double? HuberDeltaOverride { get; init; }

    /// <summary>What the last <see cref="Optimize"/> call actually used - so a sweep can report it
    /// rather than the caller having to re-derive it.</summary>
    public double LastTargetAbsorption { get; private set; }

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var or = new OptimizationResults();
        var optimization = _state.Config?.Optimization;

        // A single shared absorption value per Rx node cannot fit both same-floor pairs and
        // cross-floor pairs (e.g. separated by a concrete floor slab) well at the same time - live-
        // verified: manually lowering one node's absorption to fix its same-floor distances blew up
        // its cross-floor distances 2-4x. Since live per-floor localization already only ever uses
        // same-floor nodes (each Locator filters by Node.Floors), cross-floor node-to-node
        // measurements carry no useful signal for calibration and are excluded entirely rather than
        // just down-weighted.
        var excludedPairs = _state.Config?.Optimization?.ExcludedPairs;
        var allRxNodes = os.ByRx().SelectMany(g => g)
            .Where(n => !SpatialUtils.IsCrossFloor(n.Rx, n.Tx))
            .Where(n => !SpatialUtils.IsExcludedPair(n.Rx, n.Tx, excludedPairs))
            .ToList();

        if (allRxNodes.Count < 3)
        {
            Log.Warning("Not enough valid measurements for optimization. Found {Count} measurements, need at least 3. Add more BLE beacons or ESPresense nodes.", allRxNodes.Count);
            return or;
        }

        // Get unique Rx and Tx nodes
        var uniqueRxIds = allRxNodes.Select(n => n.Rx.Id).Distinct().ToList();
        var uniqueTxIds = allRxNodes.Select(n => n.Tx.Id).Distinct().ToList();

        // Map parameter indices
        var rxIndexMap = new Dictionary<string, int>();
        var txIndexMap = new Dictionary<string, int>();
        int paramIndex = 0;

        // Each Rx node has two parameters: rxAdjRssi and absorption
        foreach (var rxId in uniqueRxIds)
        {
            rxIndexMap[rxId] = paramIndex;
            paramIndex += 2;
        }

        // Each Tx node has one parameter: txRefRssi
        foreach (var txId in uniqueTxIds)
        {
            txIndexMap[txId] = paramIndex;
            paramIndex++;
        }

        int totalParams = paramIndex;

        if (optimization == null) return or;

        var absorptionMin = AbsorptionMinOverride ?? optimization.AbsorptionMin;
        var absorptionMax = AbsorptionMaxOverride ?? optimization.AbsorptionMax;
        // ★ The target the regularization shrinks towards. It is the midpoint of the limits unless
        // told otherwise, which conflates two statements - the limits say what absorption is
        // POSSIBLE, the target says what is LIKELY - so weights.absorption_target now separates them.
        //
        // What it does NOT do is change the default, and that is a correction to my own reasoning.
        // The argument was: limits 2.5..4.8 put the target at 3.65 while all 18 nodes fit to
        // 4.06-4.59, so the penalty drags every node down, against the data. Measured against the
        // walk points twice (26 points, then 31), pulling down is BETTER: target 3.65 scored 1.38 m
        // and 1.55 m, the fleet's own median 4.23 scored 1.39 m and 1.59 m. Small, but the same
        // direction both times. The regularization is not merely a tie-breaker being dragged off
        // course - it is moving the fit somewhere better, and "the data says 4.2" turns out to be
        // the node-to-node data, which is not what the locator is scored on.
        var targetAbsorption = AbsorptionTargetOverride
                               ?? optimization.AbsorptionTarget
                               ?? absorptionMin + (absorptionMax - absorptionMin) / 2.0;
        LastTargetAbsorption = targetAbsorption;
        double penaltyWeight = AbsorptionPenaltyOverride ?? optimization.AbsorptionPenaltyWeight;
        var useDb = string.Equals(ObjectiveOverride ?? optimization.Objective, "db", StringComparison.OrdinalIgnoreCase);
        var huberDelta = HuberDeltaOverride ?? optimization.HuberDeltaDb;

        // Pre-calculate weights for each node based on RssiVar
        var nodeWeights = new Dictionary<Measure, double>();
        double totalWeight = 0;

        foreach (var node in allRxNodes)
        {
            // Inverse variance weighting - use 1/variance as weight
            double weight = 1.0;
            if (node.RssiVar > 0)
            {
                weight = 1.0 / Math.Max(node.RssiVar.Value, 0.1); // Adding minimum to avoid extreme weights
            }

            nodeWeights[node] = weight;
            totalWeight += weight;
        }

        // Normalize weights if needed
        if (totalWeight > 0)
        {
            foreach (var node in allRxNodes)
            {
                nodeWeights[node] = nodeWeights[node] / totalWeight * allRxNodes.Count;
            }
        }

        // The original residual: squared metres, with a one-sided 4th power whenever the model
        // predicts a shorter distance than the map. Two problems, both measurable.
        //
        // The asymmetry rests on "closer than the map distance is physically impossible" - but the
        // model producing a short distance is not the device being in an impossible place, it is the
        // level reading high, which constructive multipath does routinely. And a 4th power means a
        // 3 m discrepancy weighs 81x a 1 m one, so a single reflective pair can steer the entire fit.
        //
        // Metres are the second problem: distance is exponential in level, so the same few dB of
        // noise is centimetres at 1 m and metres at 10 m. A metre-based sum is therefore dominated by
        // the pairs the model represents worst, and barely hears the ones it represents well.
        Func<double, double, double> distanceResidual = (calculated, map) =>
            calculated < map ? Math.Pow(map - calculated, 4) : Math.Pow(map - calculated, 2);

        // Huber: quadratic while the disagreement is within ordinary noise, linear beyond it. A pair
        // that contradicts the map by 30 dB then contributes proportionally rather than quadratically,
        // so it pulls without dictating - which is what "robust" has to mean here, given that the
        // diagnostics report 20 such contradictions on this installation alone.
        Func<double, double> huber = residual =>
        {
            var a = Math.Abs(residual);
            return a <= huberDelta ? 0.5 * residual * residual : huberDelta * (a - 0.5 * huberDelta);
        };

        // Both halves of ObjectiveFunction.Gradient used to carry their own copy of the error term,
        // penalty included. Two copies of one formula is one too many when the formula is the thing
        // being changed - a single Evaluate keeps the numeric gradient honest by construction.
        double Evaluate(Vector<double> x)
        {
            double error = 0;
            double weightSum = 0;

            foreach (var node in allRxNodes)
            {
                int rxBaseIndex = rxIndexMap[node.Rx.Id];
                int txBaseIndex = txIndexMap[node.Tx.Id];

                double rxAdjRssi = x[rxBaseIndex];
                double absorption = x[rxBaseIndex + 1];
                double txRefRssi = x[txBaseIndex];

                double mapDistance = node.Rx.Location.DistanceTo(node.Tx.Location);
                double weight = nodeWeights[node];
                weightSum += weight;

                if (useDb)
                {
                    // Level the model needs at the mapped distance, against the level actually
                    // measured. Same physics as the distance form, rearranged so the residual lives
                    // in the units the measurement was taken in.
                    double requiredRssi = txRefRssi - 10.0 * absorption * Math.Log10(Math.Max(mapDistance, 0.1));
                    error += weight * huber(node.GetAdjustedRssi(rxAdjRssi) - requiredRssi);
                }
                else
                {
                    double calculatedDistance = Math.Pow(10, (txRefRssi - node.GetAdjustedRssi(rxAdjRssi)) / (10.0 * absorption));
                    error += weight * distanceResidual(calculatedDistance, mapDistance);
                }

                error += weight * penaltyWeight * Math.Pow(absorption - targetAbsorption, 2);
            }

            return weightSum > 0 ? error / weightSum : error;
        }

        var objectiveFunction = ObjectiveFunction.Gradient(
            Evaluate,
            x =>
            {
                var grad = Vector<double>.Build.Dense(totalParams);
                const double h = 1e-6;
                for (int i = 0; i < totalParams; i++)
                {
                    var xPlus = x.Clone();
                    var xMinus = x.Clone();
                    xPlus[i] = x[i] + h;
                    xMinus[i] = x[i] - h;
                    grad[i] = (Evaluate(xPlus) - Evaluate(xMinus)) / (2 * h);
                }
                return grad;
            }
        );

        // Build lower and upper bound vectors
        var lowerBound = Vector<double>.Build.Dense(totalParams);
        var upperBound = Vector<double>.Build.Dense(totalParams);

        // For Rx nodes: rxAdjRssi and absorption bounds
        foreach (var rxId in uniqueRxIds)
        {
            int baseIndex = rxIndexMap[rxId];
            lowerBound[baseIndex] = optimization.RxAdjRssiMin;
            upperBound[baseIndex] = optimization.RxAdjRssiMax;
            lowerBound[baseIndex + 1] = absorptionMin;
            upperBound[baseIndex + 1] = absorptionMax;
        }

        // For Tx nodes: txRefRssi bounds
        foreach (var txId in uniqueTxIds)
        {
            lowerBound[txIndexMap[txId]] = optimization.TxRefRssiMin;
            upperBound[txIndexMap[txId]] = optimization.TxRefRssiMax;
        }

        // Initialize with a reasonable guess (ensure within bounds)
        var initialGuess = Vector<double>.Build.Dense(totalParams);
        foreach (var rxId in uniqueRxIds)
        {
            int baseIndex = rxIndexMap[rxId];
            existingSettings.TryGetValue(rxId, out var nodeSettings);
            // Clamp initial guess within global bounds
            initialGuess[baseIndex] = Math.Clamp(nodeSettings?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
            // Clamp initial guess within global bounds
            // Clamp against the EFFECTIVE (possibly overridden) bounds - stored absorption values can lie
            // outside a narrower candidate range, and BfgsBMinimizer throws on an out-of-bounds start,
            // silently voiding the whole candidate fit.
            initialGuess[baseIndex + 1] = Math.Clamp(nodeSettings?.Calibration?.Absorption ?? targetAbsorption, absorptionMin, absorptionMax);
        }
        foreach (var txId in uniqueTxIds)
        {
            existingSettings.TryGetValue(txId, out var nodeSettings);
            // Initial guess uses node setting if available, else -59
            // Clamp initial guess within global bounds
            initialGuess[txIndexMap[txId]] = Math.Clamp(nodeSettings?.Calibration?.TxRefRssi ?? -59, optimization.TxRefRssiMin, optimization.TxRefRssiMax);
        }

        try
        {
            // Use the bounded BFGS solver
            var solver = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000);
            var result = solver.FindMinimum(objectiveFunction, lowerBound, upperBound, initialGuess);

            // Process Rx node results
            foreach (var rxId in uniqueRxIds)
            {
                int baseIndex = rxIndexMap[rxId];
                double rxAdjRssi = result.MinimizingPoint[baseIndex];
                double absorption = result.MinimizingPoint[baseIndex + 1];

                // Ensure values are within bounds (should be already)
                rxAdjRssi = Math.Max(optimization.RxAdjRssiMin, Math.Min(rxAdjRssi, optimization.RxAdjRssiMax));
                absorption = Math.Max(absorptionMin, Math.Min(absorption, absorptionMax));

                var n = or.Nodes.GetOrAdd(rxId);
                n.RxAdjRssi = rxAdjRssi;
                n.Absorption = absorption;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }

            // Process Tx node results
            foreach (var txId in uniqueTxIds)
            {
                double txRefRssi = result.MinimizingPoint[txIndexMap[txId]];
                txRefRssi = Math.Max(optimization.TxRefRssiMin, Math.Min(txRefRssi, optimization.TxRefRssiMax));

                var n = or.Nodes.GetOrAdd(txId);
                n.TxRefRssi = txRefRssi;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error optimizing all nodes: {0}", ex.Message);
        }

        return or;
    }
}