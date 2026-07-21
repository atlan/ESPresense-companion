using System.Collections.Concurrent;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Services;

/// <summary>
/// Tracks per-node-pair calibration error over time so the wizard can suggest excluded_pairs
/// candidates based on PERSISTENTLY bad pairs. Fed from OptimizationRunner's snapshot sampling
/// loop (~every 30s). A pair is only suggested once it has been observed for a meaningful time
/// span AND its error stayed above the threshold for most of that time - a single noisy reading,
/// or the transient error hump while calibration re-converges after a node was physically moved,
/// must not produce a suggestion. Moving a node resets all of its pairs' statistics outright,
/// since history against the old geometry is meaningless.
/// </summary>
public class PairErrorTracker(State state)
{
    private class PairStat
    {
        public double Ewma;
        /// <summary>EWMA of the "error above threshold" indicator - the recent FRACTION of time the pair was bad.</summary>
        public double AboveFractionEwma;
        public int Samples;
        public DateTime FirstSampleAt;
        public string NodeA = "";
        public string NodeB = "";
    }

    private const double Alpha = 0.1;
    /// <summary>Slower smoothing for the above-threshold fraction so it reflects a longer recent window.</summary>
    private const double FractionAlpha = 0.05;

    private readonly ConcurrentDictionary<string, PairStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Point3D> _nodeLocations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Minimum tracked samples before a pair may be suggested.</summary>
    public const int MinSamples = 60;

    /// <summary>Minimum wall-clock observation span before a pair may be suggested.</summary>
    public static readonly TimeSpan MinObservation = TimeSpan.FromHours(2);

    /// <summary>EWMA |percent| error above which a pair counts as bad (0.4 = 40%).</summary>
    public const double SuggestionThreshold = 0.4;

    /// <summary>Fraction of recent samples that must be above threshold for a suggestion (persistence, not average).</summary>
    public const double MinAboveFraction = 0.75;

    public void Sample()
    {
        InvalidateMovedNodes();

        foreach (var (txId, tx) in state.Nodes)
        {
            if (!tx.HasLocation) continue;
            foreach (var rx in tx.RxNodes.Values)
            {
                if (!rx.Current || rx.Rx is not { HasLocation: true } rxNode) continue;
                if (!SameFloor(tx, rxNode)) continue;

                var mapDist = rx.MapDistance;
                if (mapDist <= 0 || rx.Distance <= 0) continue;

                var pct = Math.Abs((rx.Distance - mapDist) / mapDist);
                var (a, b) = string.CompareOrdinal(txId.ToLowerInvariant(), rxNode.Id.ToLowerInvariant()) <= 0
                    ? (txId, rxNode.Id)
                    : (rxNode.Id, txId);
                var stat = _stats.GetOrAdd($"{a}:{b}", _ => new PairStat { NodeA = a, NodeB = b, FirstSampleAt = DateTime.UtcNow });
                lock (stat)
                {
                    var above = pct > SuggestionThreshold ? 1.0 : 0.0;
                    if (stat.Samples == 0)
                    {
                        stat.Ewma = pct;
                        stat.AboveFractionEwma = above;
                    }
                    else
                    {
                        stat.Ewma = Alpha * pct + (1 - Alpha) * stat.Ewma;
                        stat.AboveFractionEwma = FractionAlpha * above + (1 - FractionAlpha) * stat.AboveFractionEwma;
                    }
                    stat.Samples++;
                }
            }
        }
    }

    /// <summary>
    /// Drops all statistics for pairs involving a node whose configured position changed since we
    /// last saw it - after a physical move, the error history refers to the OLD geometry and would
    /// otherwise keep (or block) suggestions for hours based on stale data.
    /// </summary>
    private void InvalidateMovedNodes()
    {
        foreach (var (id, node) in state.Nodes)
        {
            if (!node.HasLocation) continue;
            var loc = node.Location;
            if (_nodeLocations.TryGetValue(id, out var prev))
            {
                if (prev.DistanceTo(loc) > 0.01)
                {
                    _nodeLocations[id] = loc;
                    foreach (var key in _stats.Keys.ToList())
                    {
                        var parts = key.Split(':', 2);
                        if (parts.Length == 2 &&
                            (parts[0].Equals(id, StringComparison.OrdinalIgnoreCase) ||
                             parts[1].Equals(id, StringComparison.OrdinalIgnoreCase)))
                            _stats.TryRemove(key, out _);
                    }
                }
            }
            else
            {
                _nodeLocations[id] = loc;
            }
        }
    }

    public record PairErrorSnapshot(string NodeA, string NodeB, double Ewma, int Samples, TimeSpan Observed);

    /// <summary>
    /// Snapshot of all tracked pair error EWMAs - used by the wizard's placement sanity check so it
    /// judges nodes on SMOOTHED recent error instead of one instantaneous RSSI reading (which made
    /// the check flicker in and out of the validation list).
    /// </summary>
    public List<PairErrorSnapshot> GetPairErrors()
    {
        var now = DateTime.UtcNow;
        var result = new List<PairErrorSnapshot>();
        foreach (var stat in _stats.Values)
        {
            lock (stat)
                result.Add(new PairErrorSnapshot(stat.NodeA, stat.NodeB, stat.Ewma, stat.Samples, now - stat.FirstSampleAt));
        }
        return result;
    }

    public List<ExcludedPairSuggestion> GetSuggestions(IEnumerable<string>? alreadyExcluded)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in alreadyExcluded ?? Enumerable.Empty<string>())
        {
            var parts = pair.Split(':', 2);
            if (parts.Length != 2) continue;
            var a = parts[0].Trim();
            var b = parts[1].Trim();
            excluded.Add($"{a}:{b}");
            excluded.Add($"{b}:{a}");
        }

        var now = DateTime.UtcNow;
        var suggestions = new List<ExcludedPairSuggestion>();
        foreach (var (key, stat) in _stats)
        {
            double ewma, aboveFraction;
            int samples;
            DateTime firstAt;
            lock (stat)
            {
                ewma = stat.Ewma;
                aboveFraction = stat.AboveFractionEwma;
                samples = stat.Samples;
                firstAt = stat.FirstSampleAt;
            }

            var observed = now - firstAt;
            if (samples < MinSamples || observed < MinObservation) continue;
            if (ewma < SuggestionThreshold || aboveFraction < MinAboveFraction) continue;
            if (excluded.Contains(key)) continue;

            state.Nodes.TryGetValue(stat.NodeA, out var nodeA);
            state.Nodes.TryGetValue(stat.NodeB, out var nodeB);
            suggestions.Add(new ExcludedPairSuggestion
            {
                NodeA = stat.NodeA,
                NodeB = stat.NodeB,
                NodeAName = nodeA?.Name,
                NodeBName = nodeB?.Name,
                AvgAbsPercentError = ewma,
                AboveThresholdFraction = aboveFraction,
                Samples = samples,
                ObservedHours = observed.TotalHours
            });
        }

        return suggestions.OrderByDescending(s => s.AvgAbsPercentError).ToList();
    }

    private static bool SameFloor(Node a, Node b)
    {
        if (a.Floors is not { Length: > 0 } af || b.Floors is not { Length: > 0 } bf) return false;
        return af.Select(f => f.Id).Intersect(bf.Select(f => f.Id), StringComparer.OrdinalIgnoreCase).Any();
    }
}
