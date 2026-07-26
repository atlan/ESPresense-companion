using System.Collections.Concurrent;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Notices when a node changes position and records it, so the consequences stop being invisible.
///
/// Two parts of the system already react to a move on their own: <see cref="WalkTestService"/> skips
/// walk measures whose rx node has shifted since recording, and <see cref="PairErrorTracker"/> drops
/// that node's pair statistics because history against the old geometry is meaningless. Both are
/// correct and both are silent - nothing tells anyone a node moved, which measurements are no longer
/// counted, or why a node's fit suddenly wanders. Finding that out currently means comparing stored
/// coordinates against live ones by hand.
///
/// The move log is persisted for a reason: <see cref="PairErrorTracker"/> keeps its last-known
/// positions in memory only, so a node relocated while the Companion was down is never noticed there.
/// Persisting the positions here closes that blind spot for reporting purposes.
///
/// ★ Deliberately re-seeds only the absorption. rx_adj_rssi and tx_ref_rssi describe the HARDWARE -
/// receive sensitivity and transmit power - which carrying the node to another wall does not change;
/// clearing them would discard valid information and start the next fit worse. Absorption describes
/// the surroundings, and those are exactly what changed. It is re-seeded to the fleet median rather
/// than cleared, so the fit restarts from a neutral point instead of from the old room's value.
/// (Measured 2026-07-26: the optimizer re-converges within a single run either way - so this is
/// about not starting biased, not about rescue.)
/// </summary>
public class NodeMoveTracker : BackgroundService
{
    /// <summary>
    /// Shared with <see cref="WalkTestService"/>: below this a change is mounting jitter or a
    /// coordinate typo correction, not a relocation. PairErrorTracker uses a tighter 1 cm because
    /// discarding statistics is cheap and recoverable, while re-seeding calibration is not.
    /// </summary>
    public const double MoveThresholdM = 0.05;

    private const int MaxHistory = 100;

    private readonly State _state;
    private readonly NodeSettingsStore _nodeSettings;
    private readonly string? _persistPath;

    private readonly ConcurrentDictionary<string, Point3D> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<NodeMove> _history = new();
    private bool _primed;

    public NodeMoveTracker(State state, NodeSettingsStore nodeSettings, string? persistPath = null)
    {
        _state = state;
        _nodeSettings = nodeSettings;
        _persistPath = persistPath;
        Load();
    }

    public IReadOnlyList<NodeMove> GetMoves() => _history.Reverse().ToList();

    /// <summary>Moves seen within the given window - what the diagnostics report as "recent".</summary>
    public IReadOnlyList<NodeMove> GetMoves(TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        return _history.Where(m => m.At >= cutoff).Reverse().ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Node move check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task CheckAsync()
    {
        var moved = new List<NodeMove>();

        foreach (var node in _state.Nodes.Values)
        {
            if (!node.HasLocation) continue;
            var loc = node.Location;

            if (!_known.TryGetValue(node.Id, out var previous))
            {
                _known[node.Id] = loc;
                continue;
            }

            var distance = previous.DistanceTo(loc);
            if (distance <= MoveThresholdM) continue;

            _known[node.Id] = loc;

            // On the very first pass after a fresh install there is nothing to compare against
            // meaningfully; only report once we have a persisted baseline.
            if (!_primed) continue;

            var move = new NodeMove
            {
                NodeId = node.Id,
                NodeName = node.Name,
                At = DateTime.UtcNow,
                DistanceM = Math.Round(distance, 2),
                From = new[] { Math.Round(previous.X, 2), Math.Round(previous.Y, 2), Math.Round(previous.Z, 2) },
                To = new[] { Math.Round(loc.X, 2), Math.Round(loc.Y, 2), Math.Round(loc.Z, 2) }
            };
            moved.Add(move);
            _history.Enqueue(move);
            while (_history.Count > MaxHistory) _history.TryDequeue(out _);

            Log.Information("Node {Node} moved {Distance:0.00} m - pair statistics and walk measures for it are " +
                            "no longer valid; re-seeding its absorption", node.Name ?? node.Id, distance);
        }

        foreach (var move in moved)
            move.AbsorptionReseededTo = await ReseedAbsorptionAsync(move.NodeId);

        if (moved.Count > 0) Save();
        _primed = true;
    }

    /// <summary>Sets the node's absorption to the median of the other nodes, so its next fit starts
    /// from the fleet's consensus rather than from the environment it just left.</summary>
    private async Task<double?> ReseedAbsorptionAsync(string nodeId)
    {
        var others = _state.Nodes.Values
            .Where(n => !string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase))
            .Select(n => _nodeSettings.Get(n.Id)?.Calibration?.Absorption)
            .Where(a => a is > 0)
            .Select(a => a!.Value)
            .OrderBy(a => a)
            .ToList();

        if (others.Count < 3) return null;   // no meaningful consensus to fall back on

        var median = others.Count % 2 == 1
            ? others[others.Count / 2]
            : (others[others.Count / 2 - 1] + others[others.Count / 2]) / 2.0;

        var settings = _nodeSettings.Get(nodeId);
        if (settings == null) return null;
        if (settings.Calibration.Absorption is { } current && Math.Abs(current - median) < 0.01) return median;

        settings.Calibration.Absorption = Math.Round(median, 2);
        await _nodeSettings.Set(nodeId, settings);
        return Math.Round(median, 2);
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_persistPath)) return;
        try
        {
            var payload = new PersistedState
            {
                Known = _known.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.X, kv.Value.Y, kv.Value.Z }),
                History = _history.ToList()
            };
            File.WriteAllText(_persistPath, JsonConvert.SerializeObject(payload));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist node move log to {Path}", _persistPath);
        }
    }

    private void Load()
    {
        if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath)) return;
        try
        {
            var payload = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(_persistPath));
            if (payload == null) return;
            foreach (var (id, xyz) in payload.Known)
                if (xyz.Length >= 3)
                    _known[id] = new Point3D(xyz[0], xyz[1], xyz[2]);
            foreach (var move in payload.History) _history.Enqueue(move);
            // A baseline survived the restart, so a relocation that happened while we were down is
            // a real finding rather than first-run noise.
            _primed = _known.Count > 0;
            Log.Information("Loaded {Nodes} known node positions and {Moves} move records", _known.Count, _history.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read node move log from {Path}", _persistPath);
        }
    }

    private class PersistedState
    {
        public Dictionary<string, double[]> Known { get; set; } = new();
        public List<NodeMove> History { get; set; } = new();
    }
}

public class NodeMove
{
    public string NodeId { get; set; } = "";
    public string? NodeName { get; set; }
    public DateTime At { get; set; }
    public double DistanceM { get; set; }
    public double[] From { get; set; } = Array.Empty<double>();
    public double[] To { get; set; } = Array.Empty<double>();
    /// <summary>Fleet-median absorption the node was re-seeded with, if a consensus existed.</summary>
    public double? AbsorptionReseededTo { get; set; }
}
