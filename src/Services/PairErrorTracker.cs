using System.Collections.Concurrent;
using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Tracks per-node-pair calibration error over time (EWMA of |percent| distance error) so the
/// wizard can suggest excluded_pairs candidates based on PERSISTENTLY bad pairs, not one noisy
/// sample. Fed from OptimizationRunner's snapshot sampling loop (~every 30s); a single
/// /api/state/calibration reading is instantaneous and would flag transient RF noise.
/// </summary>
public class PairErrorTracker(State state)
{
    private class PairStat
    {
        public double Ewma;
        public int Samples;
        public string NodeA = "";
        public string NodeB = "";
    }

    private const double Alpha = 0.1;
    private readonly ConcurrentDictionary<string, PairStat> _stats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Minimum tracked samples (at ~30s cadence) before a pair may be suggested.</summary>
    public const int MinSamples = 10;

    /// <summary>EWMA |percent| error above which a pair becomes a suggestion (0.4 = 40%).</summary>
    public const double SuggestionThreshold = 0.4;

    public void Sample()
    {
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
                var stat = _stats.GetOrAdd($"{a}:{b}", _ => new PairStat { NodeA = a, NodeB = b });
                lock (stat)
                {
                    stat.Ewma = stat.Samples == 0 ? pct : Alpha * pct + (1 - Alpha) * stat.Ewma;
                    stat.Samples++;
                }
            }
        }
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

        var suggestions = new List<ExcludedPairSuggestion>();
        foreach (var (key, stat) in _stats)
        {
            double ewma;
            int samples;
            lock (stat)
            {
                ewma = stat.Ewma;
                samples = stat.Samples;
            }
            if (samples < MinSamples || ewma < SuggestionThreshold) continue;
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
                Samples = samples
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
