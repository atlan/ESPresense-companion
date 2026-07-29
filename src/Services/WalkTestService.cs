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
public class WalkTestService
{
    private readonly State state;
    private readonly PairErrorTracker pairErrorTracker;
    private readonly NodeSettingsStore nodeSettings;
    private readonly string? _persistPath;

    public WalkTestService(State state, PairErrorTracker pairErrorTracker, NodeSettingsStore nodeSettings, string? persistPath = null)
    {
        this.state = state;
        this.pairErrorTracker = pairErrorTracker;
        this.nodeSettings = nodeSettings;
        _persistPath = persistPath;
        LoadPoints();
    }

    /// <summary>
    /// The txRefRssi value Evaluate()/the fit fall back to for transmitters without stored
    /// settings. Walk test measures are RSSI-shifted at synthesis time so the beacon's effective
    /// reference matches this default - see RecordPoint's self-calibration.
    /// </summary>
    // internal statt private: die Wizard-Diagnose vergleicht Doppel-Aufnahmen auf
    // NORMIERTEN Pegeln und muss dafuer denselben Bezugswert benutzen. Zwei Kopien
    // derselben Zahl waeren die sicherste Art, sie auseinanderlaufen zu lassen.
    internal const double DefaultTxRefRssi = -59;

    public class RawSample
    {
        public int Tick;
        public string NodeId = "";
        public double Distance;
        public double Rssi;
        public double RefRssi;
        public double? RssiRxAdj;
        public double? DistVar;
        public double? RssiVar;
    }

    /// <summary>
    /// One raw per-tick reading, persisted with the point - the wizard's locator replay needs the
    /// REAL noisy per-sample values (what the locators see live), not the smoothed medians.
    /// </summary>
    public class RawTickEntry
    {
        public int T { get; set; }
        public string N { get; set; } = "";
        public double D { get; set; }

        /// <summary>
        /// Distanz-Varianz dieser Messung. Aufgezeichnet, weil ein Filter, der Messwerte gegen-
        /// einander prueft, wissen muss, wie zuverlaessig sie einzeln sind - die Zuverlaessigkeit
        /// schwankt in einer Anlage um zwei Groessenordnungen. Ohne sie kann ein Replay eine
        /// varianzabhaengige Toleranz nicht bewerten: er muesste fuer jede Messung denselben Wert
        /// annehmen, und dann addiert das Gewicht ueberall dieselbe Konstante. Nullable, weil
        /// aeltere Aufzeichnungen sie nicht haben.
        /// </summary>
        public double? V { get; set; }

        /// <summary>
        /// Raw level at this tick. Recorded because the distance next to it is already a DERIVED
        /// value - the node computed it from this rssi with the absorption and rssi@1m in force at
        /// the time. A replay over D alone can therefore only score the locator; anything upstream
        /// of the distance (absorption, reference level, receive adjustment) is frozen into the
        /// recording and invisible to it. With the level kept, the same points can also score
        /// calibration changes. Nullable: points recorded before this existed have no level, and
        /// the benchmark has to be able to tell the two apart rather than silently mixing them.
        /// </summary>
        public double? R { get; set; }

        /// <summary>Receive adjustment of the rx node at record time - without it the level
        /// describes the node's sensitivity as much as the distance (spans -5..+25 dB in a fleet).</summary>
        public double? A { get; set; }

        /// <summary>Reference level attributed to the transmitter at record time.</summary>
        public double? Ref { get; set; }
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
        // Rx node position at record time (plain doubles so the point JSON-serializes for
        // persistence - Point3D has no parameterless ctor). Measures are dropped if the node moves.
        public double NodeLocX { get; set; }
        public double NodeLocY { get; set; }
        public double NodeLocZ { get; set; }

        /// <summary>
        /// Stillgelegt: diese EINE Knotenmessung geht nicht mehr in Fit, Gate oder
        /// Pruefstand ein. Bewusst je Messung und nicht je Punkt - die Belege sind
        /// knotenweise (ein umgehaengter Knoten macht acht andere Messungen desselben
        /// Punkts nicht falsch), und der Bestand ist mit 10 von 32 Punkten mit Pegel
        /// zu duenn, um Brauchbares mitzuwerfen.
        ///
        /// ⚠ NICHT loeschen: Walk-Punkte sind zu Fuss erlaufene Bodenwahrheit, und die
        /// Diagnose, die sie verurteilt, kann irren - am 29.07.2026 wanderte die Zahl
        /// der "unvereinbaren" Paare an einem Tag von 9 ueber 5 auf 3, nur weil der
        /// Melder besser rechnete. Umkehrbarkeit schuetzt nicht vor der Zukunft,
        /// sondern vor der eigenen Diagnose.
        /// </summary>
        public bool Disabled { get; set; }
        /// <summary>Warum stillgelegt - im Klartext, damit ein spaeterer Lauf
        /// unterscheiden kann, was zurueckkommen darf (Modellstreit) und was nie
        /// (Knoten umgezogen, Wert physikalisch unmoeglich).</summary>
        public string? DisabledReason { get; set; }
        /// <summary>Kennung des Grundes zum maschinellen Auswerten (z.B. "few-samples").</summary>
        public string? DisabledRule { get; set; }
        public DateTime? DisabledAt { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public Point3D NodeLocationAtRecord => new(NodeLocX, NodeLocY, NodeLocZ);
    }

    public class WalkTestPoint
    {
        /// <summary>
        /// Knoten, deren Messung an diesem Punkt stillgelegt ist. Einmal je Schleife
        /// bauen statt je Roh-Tick zu suchen: die Roh-Ticks sind hundertfach, die
        /// Aggregate ein Dutzend.
        /// ⚠ Wer Raw durchgeht, MUSS hiergegen filtern - sonst ist eine Messung im
        /// Aggregat stillgelegt und im Rohverlauf weiter aktiv, und je nachdem welchen
        /// Weg ein Verbraucher nimmt, rechnet er mit anderen Daten.
        /// </summary>
        public HashSet<string> DisabledNodeIds() =>
            Nodes.Where(n => n.Disabled).Select(n => n.NodeId)
                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

        public string Id { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string? DeviceName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? FloorId { get; set; }
        public DateTime RecordedAt { get; set; }
        public List<NodeAggregate> Nodes { get; set; } = new();
        /// <summary>
        /// Self-calibrated estimate of the beacon's txRefRssi (median over same-floor nodes of
        /// rssi + 10*absorption*log10(mapDistance), using each rx node's calibration at record
        /// time). Without this, Evaluate() falls back to -59 for the unknown transmitter while a
        /// real beacon may sit at e.g. -83 - every walk measure would then carry a ~24dB baseline
        /// error, artificially degrading the baseline and biasing the accept/reject gate.
        /// </summary>
        public double? TxRefRssiEstimate { get; set; }

        /// <summary>Raw per-tick node readings for replay (real live noise, not medians).</summary>
        public List<RawTickEntry> Raw { get; set; } = new();

        /// <summary>
        /// True when the ticks carry signal levels, not just distances - only then can a replay
        /// score changes to absorption, reference level or receive adjustment. Points recorded
        /// before that was added remain fully usable for locator comparisons.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool SupportsCalibrationReplay => Raw.Any(r => r.R.HasValue);
    }

    public class ActiveSession
    {
        public string DeviceId = "";
        public string? DeviceName;
        public double X, Y, Z;
        public string? FloorId;
        public DateTime StartedAt;
        public int DurationSecs;
        public int Tick;
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
            // Project without the Raw tick series - it's replay-only data and would bloat the
            // 15s status poll (hundreds of entries per point).
            points = _points.Values.OrderBy(p => p.RecordedAt).Select(p => new
            {
                id = p.Id,
                deviceId = p.DeviceId,
                deviceName = p.DeviceName,
                x = p.X,
                y = p.Y,
                z = p.Z,
                floorId = p.FloorId,
                recordedAt = p.RecordedAt,
                nodes = p.Nodes,
                // Beides: was aufgezeichnet wurde und was heute daraus folgt. Weichen sie
                // auseinander, ist die Aufnahme aelter als die aktuelle Kalibrierung - das
                // soll man sehen koennen, statt es zu verstecken.
                txRefRssiEstimate = p.TxRefRssiEstimate,
                txRefRssiCurrent = CurrentTxRefEstimate(p),
                rawTicks = p.Raw.Count
            }).ToList(),
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
        var tick = s.Tick++;
        foreach (var (nodeId, dn) in device.Nodes)
        {
            if (!dn.Current || dn.Node is not { HasLocation: true }) continue;
            if (dn.Distance <= 0) continue;
            s.Samples.Add(new RawSample
            {
                Tick = tick,
                NodeId = nodeId,
                Distance = dn.Distance,
                Rssi = dn.Rssi,
                RefRssi = dn.RefRssi,
                RssiRxAdj = dn.RssiRxAdj,
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
                NodeLocX = node.Location.X,
                NodeLocY = node.Location.Y,
                NodeLocZ = node.Location.Z
            });
        }

        if (aggregates.Count == 0)
        {
            Log.Information("Walk test point discarded: no node collected enough samples (device {Device})", s.DeviceId);
            return null;
        }

        // Self-calibrate the beacon's reference RSSI from same-floor nodes with known distances
        // and their current calibration - only same-floor measures feed the fit anyway.
        var txRefEstimates = new List<double>();
        foreach (var agg in aggregates)
        {
            if (agg.MapDistance < 0.5) continue;
            if (!state.Nodes.TryGetValue(agg.NodeId, out var node)) continue;
            if (s.FloorId != null && !(node.Floors?.Any(f => string.Equals(f.Id, s.FloorId, StringComparison.OrdinalIgnoreCase)) ?? false)) continue;
            var absorption = nodeSettings.Get(agg.NodeId)?.Calibration?.Absorption ?? 2.7;
            txRefEstimates.Add(agg.MedianRssi + 10 * absorption * Math.Log10(agg.MapDistance));
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
            Nodes = aggregates,
            TxRefRssiEstimate = txRefEstimates.Count >= 2 ? Median(txRefEstimates) : null,
            // Keep the raw ticks only for nodes that made the aggregate cut (enough samples).
            Raw = s.Samples
                .Where(x => aggregates.Any(a => a.NodeId.Equals(x.NodeId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Tick)
                .Select(x => new RawTickEntry { T = x.Tick, N = x.NodeId, D = x.Distance, R = x.Rssi, A = x.RssiRxAdj, Ref = x.RefRssi, V = x.DistVar })
                .ToList()
        };
        _points[point.Id] = point;
        SavePoints();
        Log.Information("Walk test point {Id} recorded: {Nodes} nodes, {Samples} raw samples (device {Device} at {X},{Y},{Z})",
            point.Id, aggregates.Count, s.Samples.Count, s.DeviceId, s.X, s.Y, s.Z);
        return point;
    }

    public bool DeletePoint(string id)
    {
        var removed = _points.TryRemove(id, out _);
        if (removed) SavePoints();
        return removed;
    }

    /// <summary>All recorded points including raw tick data - for the locator replay.</summary>
    public List<WalkTestPoint> GetPoints() => _points.Values.ToList();

    /// <summary>
    /// Referenzpegel des Beacons fuer diesen Punkt, aus den HEUTIGEN Absorptionen gerechnet.
    ///
    /// ★★★ Warum nicht der gespeicherte Wert: TxRefRssiEstimate entsteht beim Aufzeichnen aus
    /// der Kalibrierung, die DAMALS galt, und friert dann ein. Die Absorptionen wandern aber
    /// weiter - und weil der Wert als rssiShift = -59 - Schaetzung in JEDE Auswertung eingeht,
    /// verschiebt ein veralteter Wert die ganze Aufnahme.
    ///
    /// ⚠ Am Bestand gemessen (29.07.2026): die gespeicherten Schaetzungen von vier Aufnahmen
    /// AM SELBEN ORT spannen 8,5 dB (wt3 -76,2 gegen wt16/23/24 rund -68). Neu gerechnet mit
    /// einheitlicher Absorption sind es 1,6 dB. Roh gemessen liegt wt3 nur 2,0 dB neben den
    /// anderen - erst die veraltete Schaetzung hebt es um 6,5 dB an und macht daraus einen
    /// "unvereinbaren Widerspruch". Beinahe haette das zum Loeschen einer voellig intakten
    /// Aufnahme gefuehrt. Derselbe Fehlertyp wie das eingefrorene Walk-Punkt-Gate vom 27.07.
    ///
    /// Rueckfall auf den gespeicherten Wert nur, wenn zu wenige Knoten fuer eine neue
    /// Schaetzung taugen - ein alter Wert ist immer noch besser als gar keiner.
    /// </summary>
    public double? CurrentTxRefEstimate(WalkTestPoint p)
    {
        var schaetzungen = new List<double>();
        foreach (var agg in p.Nodes)
        {
            if (agg.Disabled) continue;
            if (agg.MapDistance < 0.5) continue;
            if (!state.Nodes.TryGetValue(agg.NodeId, out var node)) continue;
            if (p.FloorId != null && !(node.Floors?.Any(f => string.Equals(f.Id, p.FloorId, StringComparison.OrdinalIgnoreCase)) ?? false)) continue;
            var absorption = nodeSettings.Get(agg.NodeId)?.Calibration?.Absorption ?? 2.7;
            schaetzungen.Add(agg.MedianRssi + 10 * absorption * Math.Log10(agg.MapDistance));
        }
        return schaetzungen.Count >= 2 ? Median(schaetzungen) : p.TxRefRssiEstimate;
    }

    /// <summary>
    /// Die Verschiebung, mit der die Pegel dieses Punkts auf den -59-Bezug gebracht werden,
    /// den Evaluate()/der Fit fuer unbekannte Sender ansetzen. EINE Stelle, damit nicht der
    /// eine Verbraucher die frische und der andere die eingefrorene Schaetzung benutzt.
    /// </summary>
    public double RssiShiftFor(WalkTestPoint p) =>
        CurrentTxRefEstimate(p) is { } est ? DefaultTxRefRssi - est : 0;

    private readonly object _persistLock = new();

    /// <summary>Von aussen anstossbar, wenn jemand Aggregate veraendert hat (Stilllegen).</summary>
    public void PersistPoints() => SavePoints();

    private void SavePoints()
    {
        if (_persistPath == null) return;
        try
        {
            lock (_persistLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
                File.WriteAllText(_persistPath, System.Text.Json.JsonSerializer.Serialize(_points.Values.ToList()));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist walk test points to {Path}", _persistPath);
        }
    }

    private void LoadPoints()
    {
        if (_persistPath == null || !File.Exists(_persistPath)) return;
        try
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<List<WalkTestPoint>>(File.ReadAllText(_persistPath));
            foreach (var p in loaded ?? new List<WalkTestPoint>())
            {
                _points[p.Id] = p;
                // Resume the id counter past loaded points so new ids don't collide.
                if (p.Id.StartsWith("wt") && int.TryParse(p.Id[2..], out var n) && n > _pointCounter)
                    _pointCounter = n;
            }
            if (_points.Count > 0)
                Log.Information("Loaded {Count} persisted walk test points from {Path}", _points.Count, _persistPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load persisted walk test points from {Path}", _persistPath);
        }
    }

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
            // Shift the point's RSSI values so the beacon's effective reference equals the -59
            // default that Evaluate()/the fit use for unknown transmitters (the log-distance model
            // is linear in txRefRssi, so a constant RSSI shift is exactly equivalent). Without
            // this, baseline evaluation would apply a large constant error to every walk measure.
            var rssiShift = RssiShiftFor(point);
            foreach (var agg in point.Nodes)
            {
                if (agg.Disabled) continue;
                if (!state.Nodes.TryGetValue(agg.NodeId, out var node) || !node.HasLocation) continue;
                if (node.Location.DistanceTo(agg.NodeLocationAtRecord) > NodeMoveTracker.MoveThresholdM) continue;

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
                    Rssi = agg.MedianRssi + rssiShift,
                    RssiRxAdj = null,
                    RssiVar = null,
                    RefRssi = DefaultTxRefRssi
                });
            }
        }
        return measures;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
