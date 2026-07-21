using System.Collections.Concurrent;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Guided walk test: the user places a tracked BLE device at a KNOWN position for a short session;
/// this service samples the device's live per-node RSSI/distance readings, aggregates them into a
/// "walk test point", and feeds those measures into the calibration optimizer as a temporary
/// transmitter with known coordinates. This enriches the otherwise sparse node-to-node calibration
/// data (5-8 fixed pairs per floor) with arbitrarily many, geometrically diverse reference points.
///
/// Deliberately does NOT use the existing device-anchor mechanism: an anchor's measures already
/// flow into regular optimization snapshots, which would double-count the session data, and
/// anchoring writes retained device settings over MQTT that would need careful restore. Sampling
/// Device.Nodes directly is side-effect free.
/// </summary>
public class WalkTestService(State state, PairErrorTracker pairErrorTracker)
{
    public class RawSample
    {
        public string NodeId = "";
        public double Distance;
        public double Rssi;
        public double RefRssi;
        public double? DistVar;
        public double? RssiVar;
    }

    public class NodeAggregate
    {
        public string NodeId { get; set; } = "";
        public string? NodeName { get; set; }
        public int Samples { get; set; }
        public double MedianDistance { get; set; }
        public double MedianRssi { get; set; }
        public double RefRssi { get; set; }
        public double? DistVar { get; set; }
        /// <summary>Straight-line distance from the walk point to the node - the ground truth.</summary>
        public double MapDistance { get; set; }
        /// <summary>Rx node position at record time - measures are dropped if the node moves later.</summary>
        public Point3D NodeLocationAtRecord { get; set; }
    }

    public class WalkTestPoint
    {
        public string Id { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string? DeviceName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? FloorId { get; set; }
        public DateTime RecordedAt { get; set; }
        public List<NodeAggregate> Nodes { get; set; } = new();
    }

    public class ActiveSession
    {
        public string DeviceId = "";
        public string? DeviceName;
        public double X, Y, Z;
        public string? FloorId;
        public DateTime StartedAt;
        public int DurationSecs;
        public readonly ConcurrentBag<RawSample> Samples = new();
        public CancellationTokenSource Cts = new();
    }

    private const int SampleIntervalMs = 3000;
    public const int DefaultDurationSecs = 120;
    /// <summary>Minimum samples per node for that node to make it into the recorded point.</summary>
    private const int MinSamplesPerNode = 5;

    private readonly object _sessionLock = new();
    private ActiveSession? _session;
    private readonly ConcurrentDictionary<string, WalkTestPoint> _points = new(StringComparer.OrdinalIgnoreCase);
    private int _pointCounter;

    public bool HasActiveSession => _session != null;

    public object Status()
    {
        var s = _session;
        var now = DateTime.UtcNow;

        object? active = null;
        if (s != null)
        {
            var perNode = s.Samples.GroupBy(x => x.NodeId)
                .Select(g =>
                {
                    state.Nodes.TryGetValue(g.Key, out var node);
                    var mapDist = node is { HasLocation: true }
                        ? node.Location.DistanceTo(new Point3D(s.X, s.Y, s.Z))
                        : (double?)null;
                    var medDist = Median(g.Select(x => x.Distance));
                    return new
                    {
                        nodeId = g.Key,
                        nodeName = node?.Name,
                        samples = g.Count(),
                        medianDistance = medDist,
                        mapDistance = mapDist,
                        percentError = mapDist is > 0 ? (medDist - mapDist.Value) / mapDist.Value : (double?)null
                    };
                })
                .OrderBy(x => x.nodeName ?? x.nodeId)
                .ToList();

            var elapsed = (now - s.StartedAt).TotalSeconds;
            active = new
            {
                deviceId = s.DeviceId,
                deviceName = s.DeviceName,
                x = s.X,
                y = s.Y,
                z = s.Z,
                floorId = s.FloorId,
                elapsedSecs = elapsed,
                remainingSecs = Math.Max(0, s.DurationSecs - elapsed),
                totalSamples = s.Samples.Count,
                nodes = perNode
            };
        }

        // Only deliberately TRACKED devices with current readings are walk-test candidates - merely
        // discovered BLE devices (adverts with a name, e.g. random appliances) are not something
        // the user carries to a walk-test spot, and showing them resurfaces devices the user has
        // explicitly removed from the Devices page. Fallback to any current device only when
        // nothing is tracked at all, so the feature stays usable on a fresh install.
        var candidates = state.Devices.Values
            .Where(d => !string.IsNullOrWhiteSpace(d.Id) && d.Nodes.Values.Any(dn => dn.Current))
            .ToList();
        var tracked = candidates.Where(d => d.Track).ToList();
        var devices = (tracked.Count > 0 ? tracked : candidates)
            .Select(d => new { id = d.Id, name = d.Name })
            .OrderBy(d => d.name ?? d.id)
            .ToList();

        return new
        {
            active,
            devices,
            points = _points.Values.OrderBy(p => p.RecordedAt).ToList(),
            defaultDurationSecs = DefaultDurationSecs
        };
    }

    public (bool ok, string? error) Start(string deviceId, double x, double y, double z, int? durationSecs)
    {
        lock (_sessionLock)
        {
            if (_session != null) return (false, "A walk test session is already running");
            if (!state.Devices.TryGetValue(deviceId, out var device))
                return (false, $"Device '{deviceId}' not found");
            if (!device.Nodes.Values.Any(dn => dn.Current))
                return (false, $"Device '{deviceId}' has no current node readings - is it powered and in range?");

            var floor = SpatialUtils.FindFloorContaining(new Point3D(x, y, z), state.Floors.Values);
            var s = new ActiveSession
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                X = x,
                Y = y,
                Z = z,
                FloorId = floor?.Id,
                StartedAt = DateTime.UtcNow,
                DurationSecs = Math.Clamp(durationSecs ?? DefaultDurationSecs, 30, 900)
            };
            _session = s;
            _ = RunSessionAsync(s);
            Log.Information("Walk test started: device {Device} at ({X}, {Y}, {Z}) for {Secs}s", deviceId, x, y, z, s.DurationSecs);
            return (true, null);
        }
    }

    private async Task RunSessionAsync(ActiveSession s)
    {
        try
        {
            var deadline = s.StartedAt.AddSeconds(s.DurationSecs);
            while (DateTime.UtcNow < deadline && !s.Cts.IsCancellationRequested)
            {
                SampleOnce(s);
                await Task.Delay(SampleIntervalMs, s.Cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // stop/cancel
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Walk test session sampling failed");
        }
        finally
        {
            // Auto-finish when the timer ran out (manual Stop()/Cancel() clears _session first).
            lock (_sessionLock)
            {
                if (ReferenceEquals(_session, s))
                {
                    RecordPoint(s);
                    _session = null;
                }
            }
        }
    }

    private void SampleOnce(ActiveSession s)
    {
        if (!state.Devices.TryGetValue(s.DeviceId, out var device)) return;
        foreach (var (nodeId, dn) in device.Nodes)
        {
            if (!dn.Current || dn.Node is not { HasLocation: true }) continue;
            if (dn.Distance <= 0) continue;
            s.Samples.Add(new RawSample
            {
                NodeId = nodeId,
                Distance = dn.Distance,
                Rssi = dn.Rssi,
                RefRssi = dn.RefRssi,
                DistVar = dn.DistVar,
                RssiVar = dn.RssiVar
            });
        }
    }

    public (bool ok, string? error, WalkTestPoint? point) Stop()
    {
        ActiveSession? s;
        lock (_sessionLock)
        {
            s = _session;
            if (s == null) return (false, "No walk test session is running", null);
            _session = null;
        }
        s.Cts.Cancel();
        var point = RecordPoint(s);
        return (true, null, point);
    }

    public (bool ok, string? error) Cancel()
    {
        ActiveSession? s;
        lock (_sessionLock)
        {
            s = _session;
            if (s == null) return (false, "No walk test session is running");
            _session = null;
        }
        s.Cts.Cancel();
        Log.Information("Walk test cancelled (device {Device})", s.DeviceId);
        return (true, null);
    }

    private WalkTestPoint? RecordPoint(ActiveSession s)
    {
        var aggregates = new List<NodeAggregate>();
        foreach (var g in s.Samples.GroupBy(x => x.NodeId))
        {
            if (g.Count() < MinSamplesPerNode) continue;
            if (!state.Nodes.TryGetValue(g.Key, out var node) || !node.HasLocation) continue;

            aggregates.Add(new NodeAggregate
            {
                NodeId = g.Key,
                NodeName = node.Name,
                Samples = g.Count(),
                MedianDistance = Median(g.Select(x => x.Distance)),
                MedianRssi = Median(g.Select(x => x.Rssi)),
                RefRssi = Median(g.Select(x => x.RefRssi)),
                DistVar = g.Any(x => x.DistVar.HasValue) ? Median(g.Where(x => x.DistVar.HasValue).Select(x => x.DistVar!.Value)) : null,
                MapDistance = node.Location.DistanceTo(new Point3D(s.X, s.Y, s.Z)),
                NodeLocationAtRecord = node.Location
            });
        }

        if (aggregates.Count == 0)
        {
            Log.Information("Walk test point discarded: no node collected enough samples (device {Device})", s.DeviceId);
            return null;
        }

        var point = new WalkTestPoint
        {
            Id = $"wt{Interlocked.Increment(ref _pointCounter)}",
            DeviceId = s.DeviceId,
            DeviceName = s.DeviceName,
            X = s.X,
            Y = s.Y,
            Z = s.Z,
            FloorId = s.FloorId,
            RecordedAt = DateTime.UtcNow,
            Nodes = aggregates
        };
        _points[point.Id] = point;
        Log.Information("Walk test point {Id} recorded: {Nodes} nodes, {Samples} raw samples (device {Device} at {X},{Y},{Z})",
            point.Id, aggregates.Count, s.Samples.Count, s.DeviceId, s.X, s.Y, s.Z);
        return point;
    }

    public bool DeletePoint(string id) => _points.TryRemove(id, out _);

    /// <summary>
    /// Synthesizes optimizer measures from all recorded walk test points. The device acts as a
    /// transmitter with known position; all points of one device share a single Tx id so the fit
    /// shares the beacon's txRefRssi parameter across points (per-measure locations stay per-point).
    /// Measures whose RX node has moved since recording are skipped - their RSSI was captured
    /// against the old geometry.
    /// </summary>
    public List<Measure> GetExtraMeasures()
    {
        var measures = new List<Measure>();
        foreach (var point in _points.Values)
        {
            var txFloorIds = point.FloorId != null ? new[] { point.FloorId } : null;
            foreach (var agg in point.Nodes)
            {
                if (!state.Nodes.TryGetValue(agg.NodeId, out var node) || !node.HasLocation) continue;
                if (node.Location.DistanceTo(agg.NodeLocationAtRecord) > 0.05) continue;

                var tx = new OptNode
                {
                    Id = $"walktest:{point.DeviceId}",
                    Name = $"WalkTest {point.DeviceName ?? point.DeviceId}",
                    Location = new Point3D(point.X, point.Y, point.Z),
                    FloorIds = txFloorIds
                };
                var rx = new OptNode
                {
                    Id = node.Id,
                    Name = node.Name,
                    Location = node.Location,
                    FloorIds = node.Floors?.Select(f => f.Id!).Where(id => id != null).ToArray()
                };
                measures.Add(new Measure
                {
                    Tx = tx,
                    Rx = rx,
                    Distance = agg.MedianDistance,
                    DistVar = agg.DistVar,
                    Rssi = agg.MedianRssi,
                    RssiRxAdj = null,
                    RssiVar = null,
                    RefRssi = agg.RefRssi
                });
            }
        }
        return measures;
    }

    /// <summary>
    /// Suggests placement points for the next walk test: midpoints of the same-floor node pairs
    /// with the highest smoothed calibration error (from PairErrorTracker) - measuring there gives
    /// the optimizer reference data exactly where the current fit is most uncertain.
    /// </summary>
    public List<object> SuggestPoints(int count = 3)
    {
        var suggestions = new List<object>();
        var pairs = pairErrorTracker.GetPairErrors()
            .Where(p => p.Samples >= 10)
            .OrderByDescending(p => p.Ewma)
            .ToList();

        foreach (var pair in pairs)
        {
            if (suggestions.Count >= count) break;
            if (!state.Nodes.TryGetValue(pair.NodeA, out var a) || !a.HasLocation) continue;
            if (!state.Nodes.TryGetValue(pair.NodeB, out var b) || !b.HasLocation) continue;

            var mid = new Point3D((a.Location.X + b.Location.X) / 2, (a.Location.Y + b.Location.Y) / 2, (a.Location.Z + b.Location.Z) / 2);
            var floor = SpatialUtils.FindFloorContaining(mid, state.Floors.Values)
                        ?? a.Floors?.FirstOrDefault();
            suggestions.Add(new
            {
                x = Math.Round(mid.X, 2),
                y = Math.Round(mid.Y, 2),
                z = Math.Round(mid.Z, 2),
                floorId = floor?.Id,
                floorName = floor?.Name,
                reason = $"Between '{a.Name ?? pair.NodeA}' and '{b.Name ?? pair.NodeB}' - this pair currently has {pair.Ewma:P0} average distance error",
                nodeA = a.Name ?? pair.NodeA,
                nodeB = b.Name ?? pair.NodeB,
                pairErrorPercent = pair.Ewma
            });
        }

        return suggestions;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
