using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using ESPresense.Utils;

namespace ESPresense.Services;

/// <summary>
/// Measurement-level diagnostics. <see cref="WizardService"/> validates the map (bounds, polygons,
/// node placement); this validates the radio data against that map.
///
/// The checks exist because a real accuracy investigation on 2026-07-26 needed raw MQTT, the
/// calibration matrix and hand arithmetic to find three problems the UI could have named outright:
/// a device split across two ids, a calibration parameter pinned to its configured limit, and nodes
/// reporting signal levels that no path-loss model can produce at their mapped distance. None of
/// those are "the fit is a bit off" - they are contradictions, and averaging them into an error
/// figure hides exactly the information needed to fix them.
/// </summary>
public class WizardDiagnostics(
    State state,
    NodeSettingsStore nodeSettings,
    ConfigLoader configLoader,
    DeviceIdentityTracker identityTracker,
    NodeMoveTracker moveTracker,
    WalkTestService walkTest,
    PairErrorTracker pairErrors)
{
    /// <summary>Pairs closer than this count as "near". Chosen because measured error stays within a
    /// couple of dB below it and diverges sharply above - see the field data in the roadmap.</summary>
    // ── Doppel-Aufnahmen ────────────────────────────────────────────────────
    // Ab wann gelten zwei Aufnahmen als "derselbe Ort". 1 m ist grosszuegig: die
    // Position wird von Hand auf der Karte gesetzt, ein halber Meter Ungenauigkeit
    // ist normal - und ein Widerspruch ueber diese Distanz bleibt einer.
    private const double SamePlaceRadiusM = 1.0;
    // Unter so wenigen gemeinsamen Knoten ist der Median nicht aussagekraeftig.
    private const int MinSharedNodes = 4;
    // Untergrenze fuer "erklaerbar". Die aus der Entfernungs-Streuung gerechneten
    // Werte liegen bei diesem Datenbestand meist unter 1 dB - ohne Boden wuerde
    // schon normale Funkschwankung als Widerspruch gelten. 3 dB ist die Groessen-
    // ordnung, die Koerper, Tueren und Geraete-Orientierung ohnehin ausmachen.
    private const double MinExplainableDb = 3.0;
    // Zweites Kriterium: ein EINZELNER Knoten, der so weit danebenliegt, reicht schon.
    // Der Median kann gutmuetig aussehen, waehrend ein Knoten die Zielfunktion blockiert -
    // gemessen an dieser Anlage galt wt16+wt23 mit Median 5,2 als "Rauschen" und hatte
    // dabei einen Knoten mit 24,6 dB Rest. Fuer den Optimierer zaehlt genau der: dass sich
    // neun andere Knoten einig sind, hilft ihm nicht, wenn der zehnte das Gegenteil fordert.
    // Als Vielfaches der erklaerbaren Streuung, nicht als feste dB-Zahl - sonst waere die
    // Schwelle nah am Knoten zu streng und fern zu lasch (siehe RssiSigmaDb).
    private const double SingleNodeSigmaFactor = 3.0;
    // Rueckfall, wenn ein Knoten noch gar nicht kalibriert ist. Der Bibliothekswert
    // des Pfadverlustmodells. ⚠ In einer eingelaufenen Anlage liegt die echte
    // Absorption deutlich hoeher (hier ~3,7) - und weil sie sowohl in die erklaerbare
    // Streuung als auch in den Geometrie-Term linear eingeht, aendert der Unterschied
    // die Urteile: mit 2,7 statt der kalibrierten Werte kamen bei derselben Anlage
    // 9 statt 5 unvereinbare Paare heraus. Deshalb NUR als Rueckfall verwenden.
    private const double DefaultAbsorption = 2.7;

    private const double NearFarSplitM = 4.0;

    /// <summary>Above this the measurement contradicts the map rather than merely disagreeing with it.</summary>
    private const double SignalContradictionDb = 15.0;

    /// <summary>Anteil der erlaubten Spanne, ab dem ein Wert als "dicht an der Grenze" gilt.</summary>
    private const double NearLimitFraction = 0.05;

    /// <summary>Unter so vielen Knoten sagt eine Streuungsaussage nichts.</summary>
    private const int MinNodesForSpread = 5;

    /// <summary>So wenige verschiedene Werte bei mehr Knoten = die per-Knoten-Freiheit wird nicht genutzt.</summary>
    private const int MaxCollapsedDistinct = 3;

    /// <summary>Gefittete Streuung unter diesem Anteil der geforderten = faktisch ein globaler Wert.</summary>
    private const double SpreadRatioFloor = 0.2;

    /// <summary>Ab diesem Vielfachen des Flottenmedians gilt ein Knoten als auffaellig unruhig.</summary>
    private const double NoisyNodeFactor = 8.0;

    /// <summary>Ab so viel Abweichung zwischen aufgezeichneter und heutiger Knotenposition wird gemeldet.</summary>
    private const double GeometryDriftM = 0.25;

    /// <summary>
    /// Once reported, a pair keeps being reported until it falls below this. Without the gap, a pair
    /// sitting at 14-16 dB enters and leaves the list on alternate polls: measured 2026-07-26, 8 of
    /// 39 findings flipped across three requests 12 s apart. A list that reshuffles while it is being
    /// read cannot be worked through, which is the whole point of it.
    /// </summary>
    private const double SignalReleaseDb = 12.0;

    /// <summary>
    /// Smoothing on the per-pair gap. The snapshot is instantaneous and RSSI moves several dB between
    /// polls, so the raw value both flickers across the threshold AND changes the printed number -
    /// which made every signal finding read as a new entry even when it was the same one.
    /// </summary>
    /// Measured after the fact: at 0.3 the smoothed level still crossed a whole-dB boundary between
    /// most polls, so the printed number kept moving even though the finding did not. 0.1 is roughly
    /// a ten-sample window - slow enough that the text settles, fast enough that a node genuinely
    /// changing behaviour still surfaces within a couple of minutes of polling.
    private const double SignalEwmaAlpha = 0.1;

    /// <summary>Nothing is reported before a pair has been seen this often - one stray packet is not a finding.</summary>
    private const int MinSignalObservations = 3;

    /// <summary>Worth mentioning, not yet a contradiction.</summary>
    private const double SignalSuspiciousDb = 8.0;

    /// <summary>How close to a limit still counts as sitting on it (parameters are floating point).</summary>
    private const double ClampEpsilon = 0.01;

    /// <summary>Below this a pair is too noisy to judge - avoids flagging a single stray packet.</summary>
    private const double MinMapDistanceM = 0.5;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PairSignal> _pairSignals = new();

    /// <summary>Running view of one directed pair, so findings survive a single noisy sample.</summary>
    private sealed class PairSignal
    {
        public double DeltaDb;
        public double Rssi;
        public double MeasuredDistanceM;
        public double MapDistanceM;
        public double Absorption;
        public int Observations;
        public bool Reported;
    }

    public WizardDiagnosticsResult Analyze()
    {
        var result = new WizardDiagnosticsResult { NearFarSplitM = NearFarSplitM };
        var config = configLoader.Config;

        CheckSplitIdentities(result);
        CheckNodeMoves(result);
        CheckNodeCoverage(result);
        CheckClampedParameters(config, result);
        var required = CheckRequiredAbsorption(config, result);
        CheckParameterSpread(config, required, result);
        CheckNoisyNodes(result);
        CheckWalkPointsWithoutLevels(result);
        CheckConflictingWalkPairs(result);
        CheckWalkPointGeometryDrift(result);
        AnalyzeSignals(config, result);

        return result;
    }

    /// <summary>
    /// One hardware address reporting under several device ids means its measurements are split
    /// across separate solutions. Silent by nature: both devices look healthy on their own.
    /// </summary>
    private void CheckSplitIdentities(WizardDiagnosticsResult result)
    {
        foreach (var split in identityTracker.GetSplitIdentities())
        {
            // Only splits that involve a device the user actually tracks. Two untracked ids sharing
            // an address is true, unactionable and constant - the fleet is full of phones and
            // wearables the Companion never places, and reporting those buries the one finding that
            // matters. A diagnostic list is only useful while everything on it is worth reading.
            var tracked = split.Ids.Where(i => IsTracked(i.Id)).ToList();
            if (tracked.Count == 0) continue;

            result.SplitIdentities.Add(new SplitIdentityInfo
            {
                Mac = split.Mac,
                Ids = split.Ids.Select(i => new SplitIdentityIdInfo { Id = i.Id, Nodes = i.Nodes }).ToList()
            });

            var idList = string.Join("', '", split.Ids.Select(i => $"{i.Id} ({i.Nodes.Length} nodes)"));
            // The alias target has to be a tracked device: aliasing a tracked id onto an untracked
            // one would move the measurements somewhere nothing is looking. Among tracked ids the
            // one most nodes reached wins, which is also the one that loses least by being kept.
            var main = tracked.OrderByDescending(i => i.Nodes.Length).First();
            var others = split.Ids.Where(i => !ReferenceEquals(i, main)).ToList();
            // Distinct nodes, not the sum: a node that heard BOTH ids was counted twice before, and
            // the same node appearing on both sides is the normal case rather than the exception.
            var mainNodes = main.Nodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var onlyElsewhere = others.SelectMany(o => o.Nodes).Distinct(StringComparer.OrdinalIgnoreCase)
                                      .Count(n => !mainNodes.Contains(n));
            var totalNodes = split.Ids.SelectMany(i => i.Nodes).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Category = "identity",
                Message = $"Address {split.Mac} is reporting under {split.Ids.Count} device ids: '{idList}' " +
                          $"(counted over the last {SightingWindow.TotalMinutes:0} minutes, not right now). " +
                          $"They are the same hardware. {(onlyElsewhere > 0
                              ? $"{onlyElsewhere} of {totalNodes} nodes reached only the other id and never '{main.Id}'"
                              : $"Every node also reached '{main.Id}', so nothing is being lost outright, but the " +
                                "readings are still split across two solutions")}. Publish a retained alias for the other ids, e.g. " +
                          $"espresense/settings/{others[0].Id}/config with {{\"id\":\"{main.Id}\"}} - " +
                          "aliases are keyed on the id a node derived, and nodes that resolved a different " +
                          "identifier look it up under a key that does not exist."
            });
        }

        // Same rule: a rotating address only matters for something being tracked. Every phone in the
        // house rotates, and saying so is a list of facts, not a list of problems.
        foreach (var id in identityTracker.GetRotatingIds().Where(IsTracked))
            result.RotatingAddressIds.Add(id);
    }

    /// <summary>
    /// Whether this id is one of the devices the user tracks - the same test the Devices page applies
    /// when "Show All" is off.
    /// </summary>
    private bool IsTracked(string id) =>
        state.Devices.TryGetValue(id, out var d) && d.Track;

    /// <summary>
    /// Surfaces relocations and what they cost. Both consequences are already handled correctly
    /// elsewhere - walk measures get skipped, pair statistics get dropped - but silently, so a node
    /// quietly stops contributing history and nothing says why.
    /// </summary>
    private void CheckNodeMoves(WizardDiagnosticsResult result)
    {
        foreach (var move in moveTracker.GetMoves(MoveReportWindow))
        {
            // Count what this specific move invalidated, so the message is concrete rather than a warning.
            var affectedPoints = 0;
            var affectedMeasures = 0;
            foreach (var point in walkTest.GetPoints())
            {
                var lost = point.Nodes.Count(a =>
                    string.Equals(a.NodeId, move.NodeId, StringComparison.OrdinalIgnoreCase) &&
                    state.Nodes.TryGetValue(a.NodeId, out var n) && n.HasLocation &&
                    n.Location.DistanceTo(a.NodeLocationAtRecord) > NodeMoveTracker.MoveThresholdM);
                if (lost <= 0) continue;
                affectedPoints++;
                affectedMeasures += lost;
            }

            result.NodeMoves.Add(move);

            var reseeded = move.AbsorptionReseededTo is { } a
                ? $"Its absorption was re-seeded to the fleet median ({a:0.00}) so the next fit starts neutral; "
                : "";
            var walk = affectedMeasures > 0
                ? $"{affectedMeasures} walk measures across {affectedPoints} points are no longer counted, "
                : "";

            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "moved",
                NodeId = move.NodeId,
                Message = $"Node '{move.NodeName ?? move.NodeId}' moved {move.DistanceM:0.00} m on " +
                          $"{move.At:yyyy-MM-dd HH:mm} UTC. {walk}and its pair-error history was reset - " +
                          $"measurements taken against the old geometry cannot be reused. {reseeded}" +
                          "Expect its calibration to wander for a few runs until it re-converges."
            });
        }
    }

    /// <summary>
    /// Answers the question a newcomer actually has - "where do I put a node?" - instead of only
    /// reporting how large the error is.
    ///
    /// Measured on this installation (21 walk points, 2026-07-26): where a node stood within
    /// <see cref="GoodCoverageM"/>, the median position error averaged 1.11 m; beyond that, 2.47 m.
    /// The correlation with the distance to the THIRD-nearest node was r = -0.01, so this is not
    /// about node density or overall coverage - a single node close enough is what matters. It also
    /// explained an apparent per-floor difference outright: the upper floor scored 2.73 m against
    /// the ground floor's 0.78 m purely because its measured spots sat 2.3-4.0 m from the nearest
    /// node while the ground floor's sat under 1.2 m.
    ///
    /// The room polygon is sampled on a grid rather than reduced to its centroid, because a node in
    /// one corner of a long room leaves the far end just as uncovered as no node at all.
    /// </summary>
    private void CheckNodeCoverage(WizardDiagnosticsResult result)
    {
        foreach (var floor in state.Floors.Values)
        {
            var nodes = state.Nodes.Values
                .Where(n => n.HasLocation && (n.Floors?.Any(f => f.Id == floor.Id) ?? false))
                .Select(n => n.Location)
                .ToList();
            if (nodes.Count == 0 || floor.Rooms.IsEmpty) continue;

            // Height at which a tracked device is assumed to sit. Taken from the walk points on this
            // floor when there are any - measured beats guessed - otherwise the middle of the floor.
            var walkZ = walkTest.GetPoints()
                .Where(p => string.Equals(p.FloorId, floor.Id, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Z).OrderBy(z => z).ToList();
            var deviceZ = walkZ.Count > 0
                ? walkZ[walkZ.Count / 2]
                : (floor.Bounds is { Length: >= 2 } b ? (b[0].Z + b[1].Z) / 2 : 0);

            foreach (var room in floor.Rooms.Values)
            {
                if (room.Polygon == null) continue;
                var pts = room.Polygon.Vertices.ToList();
                if (pts.Count < 3) continue;

                var minX = pts.Min(p => p.X); var maxX = pts.Max(p => p.X);
                var minY = pts.Min(p => p.Y); var maxY = pts.Max(p => p.Y);

                var distances = new List<double>();
                for (var x = minX; x <= maxX; x += SampleStepM)
                for (var y = minY; y <= maxY; y += SampleStepM)
                {
                    var p2 = new Point2D(x, y);
                    if (!room.Polygon.EnclosesPoint(p2)) continue;
                    var p3 = new Point3D(x, y, deviceZ);
                    distances.Add(nodes.Min(n => n.DistanceTo(p3)));
                }
                if (distances.Count == 0) continue;

                distances.Sort();
                var coverage = new RoomCoverage
                {
                    FloorId = floor.Id ?? "",
                    RoomId = room.Id,
                    RoomName = room.Name,
                    SampledPoints = distances.Count,
                    MedianNearestNodeM = Math.Round(distances[distances.Count / 2], 2),
                    WorstNearestNodeM = Math.Round(distances[^1], 2),
                    WellCoveredFraction = Math.Round((double)distances.Count(d => d <= GoodCoverageM) / distances.Count, 2)
                };
                result.RoomCoverage.Add(coverage);

            }
        }

        result.RoomCoverage = result.RoomCoverage.OrderByDescending(r => r.MedianNearestNodeM).ToList();

        // ★ EINE Meldung, nicht eine je Raum. Vorher standen hier 13 Zeilen, die jedes Mal
        // dasselbe sagten und sich nie aenderten - Abdeckung ist eine Eigenschaft des Gebaeudes,
        // kein Vorfall. Wer bei jedem Aufruf dreizehn unveraenderte Warnungen sieht, lernt, die
        // ganze Liste zu ueberblaettern, und uebersieht dann auch die eine, die neu ist.
        // Die Raeume selbst stehen vollstaendig in RoomCoverage - die Tabelle traegt die Details,
        // die Meldung nur den Befund.
        var duenn = result.RoomCoverage.Where(r => r.WellCoveredFraction < 0.5).ToList();
        if (duenn.Count > 0)
        {
            var schlimmste = duenn.OrderByDescending(r => r.MedianNearestNodeM).Take(3)
                                  .Select(r => $"{r.RoomName ?? r.RoomId} ({r.MedianNearestNodeM:0.0} m)");
            // Bei genau EINEM Fall bleibt die Zuordnung erhalten - erst wenn mehrere zusammenkommen,
            // gibt es keinen einen Raum mehr, auf den die Meldung zeigen koennte.
            var einzig = duenn.Count == 1 ? duenn[0] : null;
            result.Issues.Add(new ValidationIssue
            {
                Severity = duenn.Any(r => r.MedianNearestNodeM > PoorCoverageM)
                    ? ValidationSeverity.Warning : ValidationSeverity.Info,
                Category = "coverage",
                FloorId = einzig?.FloorId,
                RoomId = einzig?.RoomId,
                Message = $"In {duenn.Count} von {result.RoomCoverage.Count} Räumen liegt weniger als die halbe Fläche im " +
                          $"Umkreis von {GoodCoverageM:0.0} m um einen Knoten, am dünnsten {string.Join(", ", schlimmste)}. " +
                          "An dieser Anlage gemessen: Stellen mit einem Knoten in Reichweite hatten 1,1 m Fehler, " +
                          "Stellen ohne 2,5 m. Ein zusätzlicher Knoten in diesen Räumen bewirkt mehr als jede " +
                          "Kalibrierung. Das hängt daran, wo die Knoten hängen — es ändert sich zwischen zwei " +
                          "Läufen nicht und lässt sich durch Kalibrieren nicht beheben. Vollständige Liste in der " +
                          "Tabelle zur Raum-Abdeckung."
            });
        }
    }

    /// <summary>Grid spacing when sampling a room - fine enough to catch a long room with one node at one end.</summary>
    private const double SampleStepM = 0.5;

    /// <summary>Within this a spot measured 1.1 m median error on this installation; beyond it, 2.5 m.</summary>
    private const double GoodCoverageM = 1.5;

    /// <summary>Beyond this the room is reported as a warning rather than a note.</summary>
    private const double PoorCoverageM = 3.0;

    /// <summary>Window the identity tracker looks back over - stated in the message so the counts are not read as live.</summary>
    private static readonly TimeSpan SightingWindow = TimeSpan.FromMinutes(30);

    /// <summary>How far back a relocation is still worth reporting.</summary>
    private static readonly TimeSpan MoveReportWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// A parameter resting exactly on its limit means the optimizer wanted to go further and was not
    /// allowed to. The resulting fit is not "the best possible", it is "the best inside a box that
    /// excludes the answer" - and nothing in the UI says so today.
    /// </summary>
    private void CheckClampedParameters(Config? config, WizardDiagnosticsResult result)
    {
        var opt = config?.Optimization;
        if (opt == null) return;

        var nahAmAnschlag = new List<string>();

        foreach (var node in state.Nodes.Values)
        {
            var cal = nodeSettings.Get(node.Id)?.Calibration;
            if (cal == null) continue;

            Check(node.Id, node.Name, "absorption", cal.Absorption, opt.AbsorptionMin, opt.AbsorptionMax);
            Check(node.Id, node.Name, "rx_adj_rssi", cal.RxAdjRssi, opt.RxAdjRssiMin, opt.RxAdjRssiMax);
            Check(node.Id, node.Name, "tx_ref_rssi", cal.TxRefRssi, opt.TxRefRssiMin, opt.TxRefRssiMax);
        }

        void Check(string nodeId, string? nodeName, string name, double? value, double min, double max)
        {
            if (value is not { } v) return;

            string? bound = null;
            double limit = 0;
            if (Math.Abs(v - min) <= ClampEpsilon) { bound = "min"; limit = min; }
            else if (Math.Abs(v - max) <= ClampEpsilon) { bound = "max"; limit = max; }
            if (bound == null)
            {
                // Nicht am Anschlag, aber dicht davor. Am 27.07.2026 standen sechs Knoten bei
                // rx_adj 24 von erlaubten 25 - der Anschlag-Check schwieg voellig zu Recht, und
                // trotzdem war die Information wichtig: der Fit draengt gegen die Grenze. Deshalb
                // wird die Naehe eigens gemeldet, als Hinweis statt als Warnung.
                var span = max - min;
                if (span > 0)
                {
                    var margin = span * NearLimitFraction;
                    var nearBound = Math.Abs(v - min) <= margin ? "min"
                                  : Math.Abs(v - max) <= margin ? "max" : null;
                    if (nearBound != null)
                        nahAmAnschlag.Add($"{nodeName ?? nodeId} {name} {v:0.##} ({nearBound} {(nearBound == "min" ? min : max):0.##})");
                }
                return;
            }

            result.ClampedParameters.Add(new ClampedParameter
            {
                NodeId = nodeId, NodeName = nodeName, Parameter = name, Value = v, Limit = limit, Bound = bound
            });

        }

        // ★ EINE Meldung je BEFUND, nicht je Knoten. Die Einzelfaelle stehen vollstaendig in
        // ClampedParameters - die Tabelle traegt die Details. Sieben gleichlautende Warnungen
        // sagen nicht mehr als eine, sie verdecken nur die uebrigen Befunde.
        if (result.ClampedParameters.Count > 0)
        {
            var jeParameter = result.ClampedParameters
                .GroupBy(c => $"{c.Parameter} {c.Bound}")
                .Select(g => $"{g.Count()}x {g.Key} ({string.Join(", ", g.Take(3).Select(c => c.NodeName ?? c.NodeId))}" +
                             (g.Count() > 3 ? ", …" : "") + ")");
            var einzigerKnoten = result.ClampedParameters.Count == 1 ? result.ClampedParameters[0].NodeId : null;
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "clamped",
                NodeId = einzigerKnoten,
                Message = $"{result.ClampedParameters.Count} Knoten-Parameter stehen auf einer eingestellten Grenze: " +
                          $"{string.Join(" · ", jeParameter)}. Diese Knoten sind gedeckelt statt gefittet — nicht " +
                          "die Messungen bestimmen ihre Kalibrierung, sondern die Grenze. Erweitere den passenden " +
                          "Eintrag unter optimization.limits und lass neu rechnen, oder nimm einen Knoten heraus, " +
                          "der wirklich aus der Reihe fällt. Vollständige Liste in der Tabelle der gedeckelten " +
                          "Parameter." +
                          (nahAmAnschlag.Count > 0
                              ? $" Weitere {nahAmAnschlag.Count} stehen dicht an einer Grenze, ohne sie zu berühren: " +
                                $"{string.Join(", ", nahAmAnschlag)}."
                              : "")
            });
        }
        else if (nahAmAnschlag.Count > 0)
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Info,
                Category = "near-limit",
                Message = $"{nahAmAnschlag.Count} Knoten-Parameter stehen dicht an einer Grenze, ohne sie zu berühren: " +
                          $"{string.Join(", ", nahAmAnschlag)}. Noch ist nichts gedeckelt, aber der Fit drängt dorthin."
            });
        }
    }

    /// <summary>
    /// Doppel-Aufnahmen, die sich widersprechen.
    ///
    /// Die Frage ist NICHT "welcher Punkt ist besser" - das laesst sich aus den Daten
    /// nicht entscheiden, die Kriterien widersprechen sich (der eine ruhiger, der
    /// andere breiter aufgestellt). Die Frage ist: liegen zwei Aufnahmen so weit
    /// auseinander, dass mindestens eine falsch sein MUSS?
    ///
    /// Warum das zaehlt: Walk-Punkte gehen als zusaetzliche Referenzsender in
    /// dieselbe Zielfunktion ein wie die Knoten-Messungen. Enthaelt die Menge zwei
    /// einander ausschliessende Aussagen ueber denselben Ort, kann KEINE Kalibrierung
    /// beide erfuellen - jede Aenderung, die einer Seite hilft, wird verworfen. Das
    /// sieht aus wie "nichts zu verbessern", ist aber "unmoegliche Vorgabe".
    ///
    /// Verglichen wird auf NORMIERTEN Pegeln, genau wie die Punkte in die
    /// Zielfunktion eingehen (GetExtraMeasures verschiebt jede Aufnahme auf
    /// DefaultTxRefRssi). Der konstante Anteil faellt damit heraus; uebrig bleibt der
    /// knotenweise Widerspruch.
    /// </summary>
    private void CheckConflictingWalkPairs(WizardDiagnosticsResult result)
    {
        var pts = walkTest.GetPoints().Where(p => p.Nodes is { Count: > 0 }).ToList();
        for (var i = 0; i < pts.Count; i++)
        for (var j = i + 1; j < pts.Count; j++)
        {
            var a = pts[i];
            var b = pts[j];
            if (!string.Equals(a.FloorId, b.FloorId, StringComparison.OrdinalIgnoreCase)) continue;

            // Echte 3D-Distanz. NICHT auf x/y runden und die Etage als dritte
            // Koordinate missbrauchen: z ist die absolute Gebaeudehoehe und streut
            // INNERHALB einer Etage um ~1,5 m - zwei Punkte uebereinander waeren
            // sonst "derselbe Ort".
            var dist = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
            if (dist > SamePlaceRadiusM) continue;

            // ★★★ GAR KEINE Verschiebung, wenn beide Aufnahmen dasselbe Geraet zeigen - und das
            // ist hier der Normalfall (alle 33 Punkte dieser Anlage: ein einziger Beacon).
            //
            // Der Referenzpegel ist eine Eigenschaft des SENDERS. Zwei Aufnahmen desselben Senders
            // messen denselben Sendepegel, also kuerzt er sich beim Vergleich exakt weg. Jede
            // punktweise SCHAETZUNG dieses Pegels traegt dagegen den Fehler ihrer eigenen
            // Knotenauswahl und deren Absorptionen hinein - man vergleicht dann nicht mehr die
            // Messungen, sondern zwei Schaetzfehler obendrauf.
            //
            // ⚠ Am Bestand durchgerechnet (29.07.2026), gleiche Daten, nur diese Zeile anders:
            //     eingefrorene Schaetzung   3 unvereinbare Paare
            //     frisch gerechnet          8   (schlechter! die Schaetzung streut je Punkt:
            //                                    im Badezimmer 4,1 dB -> 11,2 dB Spanne)
            //     gar keine Verschiebung    1
            // Die Neuberechnung sah plausibel aus und war messbar schaedlich. Nachgemessen, nicht
            // begruendet - eine Begruendung haette fuer alle drei Varianten gereicht.
            //
            // Bei VERSCHIEDENEN Geraeten kuerzt sich nichts, dann bleibt nur die Schaetzung.
            var gleichesGeraet = string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase);
            var shiftA = gleichesGeraet ? 0 : walkTest.RssiShiftFor(a);
            var shiftB = gleichesGeraet ? 0 : walkTest.RssiShiftFor(b);

            var deltas = new List<double>();
            var geoms = new List<double>();
            var residuen = new List<double>();
            var explain = new List<double>();
            double maxRest = 0;
            string? maxNode = null;
            foreach (var na in a.Nodes)
            {
                if (na.Disabled) continue;
                var nb = b.Nodes.FirstOrDefault(x => string.Equals(x.NodeId, na.NodeId, StringComparison.OrdinalIgnoreCase));
                if (nb == null || nb.Disabled) continue;

                var gemessen = (na.MedianRssi + shiftA) - (nb.MedianRssi + shiftB);
                var erwartet = GeometrieDb(na, nb);
                // Der Ortsunterschied hat ein VORZEICHEN, also wird er abgezogen und
                // nicht bloss auf die Schwelle addiert. Wer weiter weg steht, MUSS
                // schwaecher messen; geht der gemessene Unterschied in die andere
                // Richtung, ist der Widerspruch groesser als der Rohwert - genau das
                // faellt bei einer blossen Schwellenanhebung unter den Tisch.
                var rest = Math.Abs(gemessen - erwartet);

                deltas.Add(Math.Abs(gemessen));
                geoms.Add(Math.Abs(erwartet));
                residuen.Add(rest);
                if (rest > maxRest) { maxRest = rest; maxNode = na.NodeId; }
                explain.Add(2.0 * Math.Sqrt(Math.Pow(RssiSigmaDb(na), 2) + Math.Pow(RssiSigmaDb(nb), 2)));
            }
            if (deltas.Count < MinSharedNodes) continue;

            var median = Median(deltas);
            var medianGeom = Median(geoms);
            var medianRest = Median(residuen);
            var explainable = Math.Max(Median(explain), MinExplainableDb);
            var floor = state.Floors.Values.FirstOrDefault(f => string.Equals(f.Id, a.FloorId, StringComparison.OrdinalIgnoreCase));
            var room = SpatialUtils.FindRoomContaining(new Point3D(a.X, a.Y, a.Z), floor);

            result.ConflictingWalkPairs.Add(new ConflictingWalkPair
            {
                IdA = a.Id, IdB = b.Id,
                RecordedAtA = a.RecordedAt, RecordedAtB = b.RecordedAt,
                FloorId = a.FloorId, FloorName = floor?.Name, RoomName = room?.Name,
                X = Math.Round(a.X, 2), Y = Math.Round(a.Y, 2), Z = Math.Round(a.Z, 2),
                DistanceM = Math.Round(dist, 2),
                SharedNodes = deltas.Count,
                MedianDeltaDb = Math.Round(median, 1),
                MedianGeometryDb = Math.Round(medianGeom, 1),
                MedianResidualDb = Math.Round(medianRest, 1),
                MaxDeltaDb = Math.Round(maxRest, 1),
                MaxDeltaNode = maxNode,
                ExplainableDb = Math.Round(explainable, 1),
                Irreconcilable = medianRest > explainable || maxRest > SingleNodeSigmaFactor * explainable,
                // Woran es liegt, damit die Empfehlung spaeter das Richtige vorschlaegt:
                // beim Median ist die ganze Aufnahme verdaechtig, beim Einzelknoten nur
                // dessen Messung - und dann reicht es, DIESE stillzulegen.
                SingleNodeOnly = medianRest <= explainable && maxRest > SingleNodeSigmaFactor * explainable
            });
        }

        // Nach Schwere sortieren, damit oben steht, was am dringendsten weg muss.
        result.ConflictingWalkPairs = result.ConflictingWalkPairs
            .OrderByDescending(p => p.Irreconcilable)
            .ThenByDescending(p => p.MedianResidualDb)
            .ToList();

        var unvereinbar = result.ConflictingWalkPairs.Count(p => p.Irreconcilable);
        if (unvereinbar == 0) return;

        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Warning,
            Category = "conflicting-walkpoints",
            Message = $"{unvereinbar} Paare von Walk-Punkten liegen keinen Meter auseinander und widersprechen sich " +
                      $"trotzdem stärker, als die Messstreuung erklären kann. Sie gehen als zusätzliche " +
                      $"Referenzsender in den Optimierer ein, also kann keine Kalibrierung beide erfüllen — jede " +
                      $"Änderung, die der einen Seite hilft, schadet der anderen und wird verworfen. Das sieht aus " +
                      $"wie „nichts zu verbessern“ und ist in Wahrheit „unerfüllbares Ziel“."
        });
    }

    /// <summary>
    /// Wieviel Pegelunterschied allein daher kommt, dass die beiden Aufnahmen
    /// verschieden weit von DIESEM Knoten entfernt sind (dB, mit Vorzeichen:
    /// erwartetes rssiA - rssiB).
    ///
    /// ⚠ Ohne diesen Abzug meldet der Melder Widersprueche, wo keine sind. Das
    /// Modell ist logarithmisch, also zaehlt das VERHAELTNIS der Entfernungen,
    /// nicht ihre Differenz: zu einem Knoten in 10 m Entfernung sind 0,95 m
    /// Versatz rund 0,6 dB, zu einem Knoten in 0,9 m dagegen 5,6 dB. Ein fester
    /// Toleranzzuschlag kann das nicht abbilden - er waere fern zu grosszuegig
    /// und nah zu streng.
    ///
    /// Genommen wird die AUFGEZEICHNETE Kartendistanz, nicht die heutige: wurde
    /// der Knoten zwischen den Aufnahmen umgehaengt, beschreibt jede Aufnahme
    /// zu Recht ihre eigene Geometrie (siehe CheckWalkPointGeometryDrift).
    /// </summary>
    private double GeometrieDb(WalkTestService.NodeAggregate na, WalkTestService.NodeAggregate nb)
    {
        var da = na.MapDistance;
        var db = nb.MapDistance;
        // Ohne brauchbare Kartendistanz lieber null zurueckgeben als raten - dann
        // bleibt es beim reinen Messvergleich wie bisher.
        if (da <= 0.1 || db <= 0.1) return 0;
        var a = nodeSettings.Get(na.NodeId)?.Calibration?.Absorption ?? DefaultAbsorption;
        return 10.0 * a * Math.Log10(db / da);
    }

    /// <summary>
    /// Entfernungs-Streuung (m) in Pegel-Streuung (dB) umrechnen.
    ///
    /// ⚠ DistVar ist die Varianz der ENTFERNUNG in Metern, nicht des Pegels - eine
    /// RSSI-Varianz wird je Aggregat gar nicht aufgezeichnet (RssiVar bleibt null).
    /// Wer sqrt(DistVar) direkt als dB nimmt, mischt Meter und Dezibel. Die
    /// Umrechnung liefert das Pfadverlustmodell selbst:
    ///     rssi = ref - 10*n*log10(d)   =>   |drssi/dd| = 10*n / (d*ln10)
    /// Dieselbe Meter-Streuung bedeutet nahe am Knoten VIEL mehr Dezibel als weit
    /// weg: bei sigma_d = 0,25 m sind es auf 1 m rund 2,9 dB, auf 10 m nur 0,3 dB.
    /// Genau deshalb taugt eine feste dB-Schwelle nicht.
    /// </summary>
    private double RssiSigmaDb(WalkTestService.NodeAggregate n)
    {
        var d = n.MedianDistance;
        // DistVar ist nullable: aeltere Aufnahmen haben gar keine Varianz. Dann
        // traegt der Knoten nichts zur Schwelle bei und der Mindestwert greift -
        // besser als eine erfundene Streuung.
        var varianz = n.DistVar ?? 0;
        if (d <= 0.1 || varianz <= 0) return 0;
        var absorption = nodeSettings.Get(n.NodeId)?.Calibration?.Absorption ?? DefaultAbsorption;
        return 10.0 * absorption * Math.Sqrt(varianz) / (d * Math.Log(10));
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    /// <summary>
    /// Walk-Punkte ohne aufgezeichnete Pegel - die Nachhol-Liste.
    ///
    /// Solche Punkte koennen den LOCATOR bewerten, aber keine Kalibrierung: ohne Pegel laesst sich
    /// die Distanz nicht neu rechnen, sie bleibt auf dem Wert eingefroren, den der Knoten damals
    /// gemeldet hat. In einem Lauf mit Overrides mischen sie sich stumm unter die auswertbaren
    /// Punkte und verduennen jede Aussage - am 27.07.2026 waren es 22 von 32 Punkten, also 59 % der
    /// Ticks, die auf keine Aenderung reagieren konnten.
    ///
    /// Deshalb wird hier NAMENTLICH aufgelistet, was neu abgegangen werden muss. Eine Zahl allein
    /// ("22 Punkte veraltet") laesst sich nicht abarbeiten, eine Liste mit Raum und Koordinaten
    /// schon - und nach jedem Spaziergang wird sie kuerzer.
    /// </summary>
    private void CheckWalkPointsWithoutLevels(WizardDiagnosticsResult result)
    {
        var stale = new List<WalkTestService.WalkTestPoint>();
        var total = 0;
        foreach (var p in walkTest.GetPoints())
        {
            if (p.Raw.Count == 0) continue;
            total++;
            if (!p.Raw.Any(e => e.R.HasValue && e.Ref.HasValue)) stale.Add(p);
        }
        if (total == 0 || stale.Count == 0) return;

        foreach (var p in stale.OrderBy(p => p.FloorId).ThenBy(p => p.Id))
        {
            var floor = state.Floors.Values.FirstOrDefault(f => string.Equals(f.Id, p.FloorId, StringComparison.OrdinalIgnoreCase));
            var room = SpatialUtils.FindRoomContaining(new Point3D(p.X, p.Y, p.Z), floor);
            result.StaleWalkPoints.Add(new StaleWalkPoint
            {
                Id = p.Id, FloorId = p.FloorId, FloorName = floor?.Name, RoomName = room?.Name,
                X = Math.Round(p.X, 2), Y = Math.Round(p.Y, 2), Z = Math.Round(p.Z, 2),
                Ticks = p.Raw.Count, RecordedAt = p.RecordedAt
            });
        }

        // Der Satz bleibt - er erklaert das WARUM. Die Punkte selbst stehen jetzt strukturiert
        // daneben, damit die Oberflaeche eine Tabelle daraus machen kann.
        result.Issues.Add(new ValidationIssue
        {
            Severity = ValidationSeverity.Info,
            Category = "stale-walkpoints",
            Message = $"{stale.Count} von {total} Walk-Punkten tragen keine aufgezeichneten Pegel. Sie können damit den " +
                      $"Locator prüfen, aber keine Kalibrierung bewerten — auf eine geänderte Kalibrierung " +
                      $"reagieren sie gar nicht. Der Prüfstand nennt beide Zahlen getrennt, damit die eine nicht " +
                      $"für die andere gehalten wird. Es ist nichts zu tun; die Punkte sind in Ordnung."
        });
    }

    /// <summary>
    /// Walk-Punkte, die eine ueberholte Geometrie beschreiben.
    ///
    /// Jeder Punkt speichert die Knotenpositionen von der Aufnahme mit. Wird ein Knoten spaeter
    /// umgesetzt, beschreiben die alten Punkte fuer ihn eine Welt, die es nicht mehr gibt - die
    /// Messung bleibt gueltig, nur nicht mehr fuer die heutige Entfernung. Am 27.07.2026 stand der
    /// Kitchen-Knoten in 12 Punkten bis zu 1,76 m woanders.
    ///
    /// Das ist KEIN Migrationsartefakt, sondern der Normalfall im Betrieb: Knoten werden umgehaengt.
    /// Der <see cref="NodeMoveTracker"/> meldet einen Umzug zwar, wenn er ihn miterlebt - er legt
    /// beim allerersten Lauf aber nur seine Grundlinie an und schweigt (siehe _primed dort), und was
    /// davor passiert ist, sieht er nie. Dieser Vergleich hier braucht keine Vorgeschichte: er haelt
    /// die Aufzeichnung gegen den Ist-Zustand und findet die Abweichung auch nachtraeglich.
    /// </summary>
    private void CheckWalkPointGeometryDrift(WizardDiagnosticsResult result)
    {
        var drift = new Dictionary<string, (int Points, double MaxM)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in walkTest.GetPoints())
        foreach (var agg in p.Nodes)
        {
            // Schon stillgelegte Messungen nicht erneut melden - sonst steht in der
            // Diagnose auf Dauer, was bereits erledigt ist.
            if (agg.Disabled) continue;
            if (!state.Nodes.TryGetValue(agg.NodeId, out var node) || !node.HasLocation) continue;
            var then = new Point3D(agg.NodeLocX, agg.NodeLocY, agg.NodeLocZ);
            var d = then.DistanceTo(node.Location);
            if (d <= GeometryDriftM) continue;
            var cur = drift.TryGetValue(agg.NodeId, out var v) ? v : (0, 0.0);
            drift[agg.NodeId] = (cur.Item1 + 1, Math.Max(cur.Item2, d));
        }

        foreach (var (nodeId, (points, maxM)) in drift.OrderByDescending(kv => kv.Value.Points))
        {
            var name = state.Nodes.TryGetValue(nodeId, out var n) ? n.Name ?? nodeId : nodeId;
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "geometry-drift",
                NodeId = nodeId,
                Message = $"Knoten „{name}“ wurde versetzt, seit {points} Walk-Punkte aufgezeichnet wurden — um bis zu " +
                          $"{maxM:0.00} m. Diese Punkte beschreiben ihn noch am alten Platz. Die Entfernungen " +
                          $"stammen deshalb aus der Aufzeichnung und nicht aus der heutigen Karte, der Fit bleibt " +
                          $"also richtig. Nur alles, was gegen den HEUTIGEN Aufbau bewertet wird, ist nur so " +
                          $"aktuell wie diese Punkte."
            });
        }
    }

    /// <summary>
    /// Was fuer eine Absorption braeuchte jeder Knoten, damit sein gemessener Pegel zur BEKANNTEN
    /// Entfernung des Walk-Punkts passt?
    ///
    /// Das ist die Frage, die am 27.07.2026 die Diagnose lieferte und die kein Panel beantwortete.
    /// Sie trennt zwei Faelle, die sonst gleich aussehen: eine Kalibrierung, die noch nicht
    /// konvergiert ist (dann liegt der geforderte Wert INNERHALB der Grenzen und weiteres Optimieren
    /// hilft), und ein Weglaengenmodell, das diesen Knoten prinzipiell nicht abbilden kann (dann
    /// liegt er ausserhalb, und kein Optimierer-Lauf der Welt bringt etwas). Gemessen wurde damals
    /// eine Forderung von 1,67 bis 14,1 bei erlaubten 2,5 bis 4,8.
    ///
    /// Anders als AnalyzeSignals, das Knoten gegen Knoten rechnet, benutzt das hier die einzige
    /// echte Bodenwahrheit im System: eine vom Menschen eingetragene Geraeteposition.
    /// </summary>
    private Dictionary<string, double> CheckRequiredAbsorption(Config? config, WizardDiagnosticsResult result)
    {
        var required = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var opt = config?.Optimization;
        if (opt == null) return required;

        var samples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in walkTest.GetPoints())
        {
            if (point.Raw.Count == 0) continue;
            var truth = new Point3D(point.X, point.Y, point.Z);

            var stillgelegt = point.DisabledNodeIds();
            foreach (var e in point.Raw)
            {
                if (stillgelegt.Contains(e.N)) continue;
                if (e.R is not { } rssi || e.Ref is not { } refRssi) continue;
                if (!state.Nodes.TryGetValue(e.N, out var node) || !node.HasLocation) continue;

                var trueDist = node.Location.DistanceTo(truth);
                var logD = Math.Log10(trueDist);
                // Auf einem Meter faellt der Distanzterm weg, dort ist die Absorption nicht bestimmbar.
                if (trueDist <= 0 || Math.Abs(logD) < 0.05) continue;

                var a = (refRssi - rssi) / (10.0 * logD);
                if (double.IsNaN(a) || double.IsInfinity(a)) continue;
                samples.TryAdd(e.N, new List<double>());
                samples[e.N].Add(a);
            }
        }

        var ausserhalb = new List<(string Name, double Median)>();
        var ausserhalbIds = new List<string>();
        foreach (var (nodeId, values) in samples)
        {
            if (values.Count < 10) continue;   // zu duenn, um daraus etwas zu schliessen
            values.Sort();
            var median = values[values.Count / 2];
            required[nodeId] = median;

            if (median >= opt.AbsorptionMin && median <= opt.AbsorptionMax) continue;

            var name = state.Nodes.TryGetValue(nodeId, out var n) ? n.Name ?? nodeId : nodeId;
            ausserhalb.Add((name, median));
            ausserhalbIds.Add(nodeId);
        }

        // ★ EINE Meldung. Acht gleichlautende Warnungen waren dieselbe Aussage achtmal: das
        // Pfadverlustmodell kann diese Knoten nicht abbilden. Wichtig ist, WIE VIELE und in
        // welche Richtung - das ist ein systemischer Befund ueber die Anlage, kein Vorfall
        // je Knoten.
        if (ausserhalb.Count > 0)
        {
            var drunter = ausserhalb.Where(x => x.Median < opt.AbsorptionMin)
                                    .OrderBy(x => x.Median).ToList();
            var drueber = ausserhalb.Where(x => x.Median > opt.AbsorptionMax)
                                    .OrderByDescending(x => x.Median).ToList();
            string Liste(List<(string Name, double Median)> l) =>
                string.Join(", ", l.Take(4).Select(x => $"{x.Name} {x.Median:0.0}")) + (l.Count > 4 ? ", …" : "");

            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "model-limit",
                NodeId = ausserhalb.Count == 1 ? ausserhalbIds[0] : null,
                Message = $"{ausserhalb.Count} Knoten bräuchten eine Absorption außerhalb der erlaubten " +
                          $"{opt.AbsorptionMin:0.0}..{opt.AbsorptionMax:0.0}, damit ihre Pegel zu den Entfernungen " +
                          $"passen, in denen tatsächlich gemessen wurde" +
                          (drunter.Count > 0 ? $" - {drunter.Count} below the minimum ({Liste(drunter)})" : "") +
                          (drueber.Count > 0 ? $"{(drunter.Count > 0 ? " and" : " -")} {drueber.Count} above the maximum ({Liste(drueber)})" : "") +
                          ". No optimizer run can reach those values, so this is not a calibration that has not " +
                          "converged: the path-loss model cannot represent these nodes. " +
                          (drunter.Count >= 3
                              ? "Several nodes below the minimum at once points at the limit itself rather than at " +
                                "the individual nodes - but note that lowering absorption_min was measured on this " +
                                "installation and made positioning WORSE, so widen it only with a benchmark run. "
                              : "") +
                          "Look at where those nodes sit and what stands between them and the room."
            });
        }

        return required;
    }

    /// <summary>
    /// Wird die per-Knoten-Freiheit ueberhaupt genutzt?
    ///
    /// Ein Optimierer mit 18 freien Absorptionen, der 18-mal praktisch denselben Wert liefert, ist
    /// ein globaler Optimierer mit 18-fachem Aufwand - und niemand sieht es, weil jede Zahl fuer sich
    /// plausibel aussieht. Am 27.07.2026 lagen alle 18 Knoten zwischen 4,14 und 4,33, waehrend die
    /// Walk-Punkte Werte von 1,67 bis 14,1 verlangten. Zwei unabhaengige Anzeichen werden geprueft:
    /// zu wenige VERSCHIEDENE Werte (rx_adj hatte drei fuer 18 Knoten), und eine Streuung, die
    /// gegenueber der geforderten verschwindet.
    /// </summary>
    private void CheckParameterSpread(Config? config, Dictionary<string, double> required,
        WizardDiagnosticsResult result)
    {
        var opt = config?.Optimization;
        if (opt == null) return;

        var absorption = new List<double>();
        var rxAdj = new List<double>();
        foreach (var node in state.Nodes.Values)
        {
            var cal = nodeSettings.Get(node.Id)?.Calibration;
            if (cal?.Absorption is { } a) absorption.Add(a);
            if (cal?.RxAdjRssi is { } r) rxAdj.Add(r);
        }

        // ★ Alles hier haengt an einer Vorbedingung: es muss Bodenwahrheit geben, die zeigt, dass die
        // Anlage ueberhaupt uneinheitlich IST. Ohne sie ist "alle Knoten haben denselben Wert" kein
        // Befund, sondern der Normalzustand einer frisch aufgesetzten Installation, in der noch nie
        // etwas gefittet wurde - und eine Diagnose, die dort schon meckert, bringt man dem Benutzer
        // bei zu ignorieren. (Ein Test hat genau diesen Fehlalarm gefangen.)
        if (required.Count < MinNodesForSpread) return;

        Distinct("rx_adj_rssi", rxAdj);
        Distinct("absorption", absorption);

        // Gefittete gegen geforderte Streuung - nur aussagekraeftig, wenn genug Knoten beides haben.
        if (absorption.Count >= MinNodesForSpread)
        {
            var fittedSpread = absorption.Max() - absorption.Min();
            var req = required.Values.OrderBy(v => v).ToList();
            // 10./90. Perzentil statt min/max: ein einziger pathologischer Knoten soll die Aussage
            // nicht allein tragen.
            var requiredSpread = req[(int)(0.9 * (req.Count - 1))] - req[(int)(0.1 * (req.Count - 1))];
            if (requiredSpread > 0 && fittedSpread < requiredSpread * SpreadRatioFloor)
                result.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    Category = "spread",
                    Message = $"Die Absorption spannt über {absorption.Count} Knoten nur {fittedSpread:0.00}, während die " +
                              $"Walk-Punkte eine Spanne von {requiredSpread:0.00} verlangen. Der Optimierer mit " +
                              $"Freiheit je Knoten liefert damit, was ein globaler auch liefern würde — bei " +
                              $"{absorption.Count}-facher Zahl freier Parameter. Der Hebel dagegen heißt " +
                              $"weights.absorption_penalty."
                });
        }

        void Distinct(string name, List<double> values)
        {
            if (values.Count < MinNodesForSpread) return;
            var distinct = values.Select(v => Math.Round(v, 2)).Distinct().Count();
            if (distinct > MaxCollapsedDistinct) return;
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "spread",
                Message = $"{values.Count} nodes share only {distinct} distinct {name} value" +
                          $"{(distinct == 1 ? "" : "s")}. A per-node parameter that takes {distinct} values is not " +
                          $"being fitted per node - either the penalty pulls them together or the optimizer is " +
                          $"converging into a few basins. Worth knowing before tuning anything downstream of it."
            });
        }
    }

    /// <summary>
    /// Knoten, deren Pegel unruhiger sind als der Rest der Flotte.
    ///
    /// Am 27.07.2026 hatte der Labor-Knoten rssiVar 28,7 gegen 0,2 bis 3,7 bei allen anderen - Faktor
    /// zehn bis hundert - und war ausgerechnet der naechstgelegene, also derjenige, der die Loesung
    /// haette festnageln muessen. Kein Panel zeigte es. Ein unruhiger Pfad ist etwas anderes als ein
    /// falsch kalibrierter: kein Parameter repariert Mehrwegeausbreitung, da hilft nur Umhaengen.
    /// </summary>
    private void CheckNoisyNodes(WizardDiagnosticsResult result)
    {
        var perNode = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in state.Devices.Values)
        foreach (var (nodeId, dn) in device.Nodes)
        {
            if (dn.RssiVar is not { } v || v <= 0) continue;
            perNode.TryAdd(nodeId, new List<double>());
            perNode[nodeId].Add(v);
        }

        var medians = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (nodeId, values) in perNode)
        {
            if (values.Count < 3) continue;
            values.Sort();
            medians[nodeId] = values[values.Count / 2];
        }
        if (medians.Count < MinNodesForSpread) return;

        var fleet = medians.Values.OrderBy(v => v).ToList();
        var fleetMedian = fleet[fleet.Count / 2];
        if (fleetMedian <= 0) return;

        foreach (var (nodeId, med) in medians.OrderByDescending(kv => kv.Value))
        {
            if (med < fleetMedian * NoisyNodeFactor) continue;
            var name = state.Nodes.TryGetValue(nodeId, out var n) ? n.Name ?? nodeId : nodeId;
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "noisy",
                NodeId = nodeId,
                Message = $"Knoten „{name}“: Pegelstreuung {med:0.0}, rund das {med / fleetMedian:0}-fache des " +
                          $"Flottenmedians von {fleetMedian:0.0}. Seine Messwerte streuen weit stärker als die " +
                          $"aller anderen, und das kann kein Kalibrierparameter auffangen — es liegt am Weg " +
                          $"(Metall, ein Gehäuse, etwas dazwischen). Am meisten schadet es, wenn ausgerechnet " +
                          $"dieser Knoten dem Gerät am nächsten ist."
            });
        }
    }

    /// <summary>
    /// Compares each pair's measured level with the level the path-loss model would need to produce
    /// the mapped distance, and reports fit quality separately for near and far pairs.
    ///
    /// Working in dB rather than metres is deliberate: the same few dB of noise is a few centimetres
    /// up close and several metres far away, so a metre-based figure is dominated by the pairs the
    /// model can least represent, and says little about the ones it can.
    /// </summary>
    private void AnalyzeSignals(Config? config, WizardDiagnosticsResult result)
    {
        var snapshot = state.TakeOptimizationSnapshot();
        if (snapshot.Measures.Count == 0) return;

        // How long a pair has been misbehaving decides what to do about it, and nothing said so far.
        // A pair that has read 20 dB wrong for the three weeks it has been watched is a placement or
        // hardware matter - no calibration will absorb it. The same 20 dB since yesterday is an
        // event: something was moved, plugged in, or closed. Same number, opposite response.
        var history = pairErrors.GetPairErrors().ToDictionary(
            e => $"{e.NodeA}\u0000{e.NodeB}", e => e, StringComparer.OrdinalIgnoreCase);

        var opt = config?.Optimization;
        var fallbackAbsorption = opt == null ? 3.0 : opt.AbsorptionMin + (opt.AbsorptionMax - opt.AbsorptionMin) / 2.0;

        var near = new List<Sample>();
        var far = new List<Sample>();

        foreach (var m in snapshot.Measures)
        {
            var mapDistance = m.Rx.Location.DistanceTo(m.Tx.Location);
            if (mapDistance < MinMapDistanceM || m.Distance <= 0) continue;

            var absorption = nodeSettings.Get(m.Rx.Id)?.Calibration?.Absorption ?? fallbackAbsorption;
            if (absorption <= 0) continue;

            // Level the model needs at the mapped distance; the measured level minus this is the
            // part the model cannot explain.
            var requiredRssi = m.RefRssi - 10.0 * absorption * Math.Log10(mapDistance);
            var deltaDb = m.Rssi - requiredRssi;

            var key = $"{m.Rx.Id}\u0000{m.Tx.Id}";
            var signal = _pairSignals.GetOrAdd(key, _ => new PairSignal());
            lock (signal)
            {
                if (signal.Observations == 0)
                {
                    signal.DeltaDb = deltaDb;
                    signal.Rssi = m.Rssi;
                    signal.MeasuredDistanceM = m.Distance;
                }
                else
                {
                    signal.DeltaDb = SignalEwmaAlpha * deltaDb + (1 - SignalEwmaAlpha) * signal.DeltaDb;
                    signal.Rssi = SignalEwmaAlpha * m.Rssi + (1 - SignalEwmaAlpha) * signal.Rssi;
                    signal.MeasuredDistanceM = SignalEwmaAlpha * m.Distance + (1 - SignalEwmaAlpha) * signal.MeasuredDistanceM;
                }
                // Geometry and calibration are not measurements - no reason to smooth them.
                signal.MapDistanceM = mapDistance;
                signal.Absorption = absorption;
                signal.Observations++;

                // Summaries built from the smoothed pair values rather than the raw sample: it makes
                // the near/far figures stop wandering, and it weights every pair once instead of
                // letting a chatty pair count more than a quiet one.
                var sample = new Sample(signal.DeltaDb, signal.MeasuredDistanceM - mapDistance);
                (mapDistance <= NearFarSplitM ? near : far).Add(sample);

                if (signal.Observations < MinSignalObservations) continue;

                var magnitude = Math.Abs(signal.DeltaDb);
                signal.Reported = signal.Reported ? magnitude >= SignalReleaseDb : magnitude >= SignalContradictionDb;

                if (magnitude < SignalSuspiciousDb) continue;

                result.SignalOutliers.Add(new SignalOutlier
                {
                    RxId = m.Rx.Id, RxName = m.Rx.Name,
                    TxId = m.Tx.Id, TxName = m.Tx.Name,
                    MapDistanceM = Math.Round(mapDistance, 2),
                    MeasuredDistanceM = Math.Round(signal.MeasuredDistanceM, 2),
                    MeasuredRssi = Math.Round(signal.Rssi, 1),
                    RequiredRssi = Math.Round(requiredRssi, 1),
                    DeltaDb = Math.Round(signal.DeltaDb, 1),
                    Absorption = Math.Round(absorption, 2),
                    Observations = signal.Observations,
                    Reported = signal.Reported,
                    ObservedHours = Lookup(history, m.Rx.Id, m.Tx.Id) is { } h ? Math.Round(h.Observed.TotalHours, 1) : null
                });
            }
        }

        result.Near = Summarize(near);
        result.Far = Summarize(far);
        // Tie-broken on the pair id so the cut at 20 does not reorder when two pairs sit at the same
        // rounded magnitude - otherwise the trimming itself becomes a source of churn.
        result.SignalOutliers = result.SignalOutliers
            .OrderByDescending(o => Math.Abs(o.DeltaDb))
            .ThenBy(o => o.RxId, StringComparer.Ordinal)
            .ThenBy(o => o.TxId, StringComparer.Ordinal)
            .Take(20)
            .ToList();

        // ★ EINE Meldung statt bis zu zwanzig. Alle sagen dasselbe: dieses Knotenpaar misst
        // etwas, das kein Pfadverlust erklaert. Die Einzelfaelle stehen vollstaendig in
        // SignalOutliers - dort mit Pegeln, Entfernungen und Alter. Zwanzig Textzeilen davor
        // machten aus einem Befund eine Wand, hinter der die uebrigen Befunde verschwanden.
        var widersprueche = result.SignalOutliers.Where(s => s.Reported).ToList();
        if (widersprueche.Count > 0)
        {
            var schlimmste = widersprueche.OrderByDescending(o => Math.Abs(o.DeltaDb)).Take(3)
                .Select(o => $"{o.RxName ?? o.RxId} → {o.TxName ?? o.TxId} ({o.DeltaDb:+0;-0} dB)");
            // Welcher Knoten steckt am haeufigsten drin? Wenn EINER die Liste dominiert, ist er
            // die Ursache - und das ist die eigentlich handlungsleitende Information.
            var haeufigster = widersprueche.GroupBy(o => o.RxName ?? o.RxId)
                .OrderByDescending(g => g.Count()).First();
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "signal",
                NodeId = widersprueche.Count == 1 ? widersprueche[0].RxId : null,
                Message = $"{widersprueche.Count} Knotenpaare messen einen Pegel, den kein Pfadverlust erklärt — " +
                          $"am stärksten {string.Join(", ", schlimmste)}. " +
                          (haeufigster.Count() >= 3
                              ? $"„{haeufigster.Key}“ steckt in {haeufigster.Count()} davon — sieh dir zuerst diesen Knoten an: " +
                                "eingetragene Position, Antenne, was um ihn herum steht. "
                              : "") +
                          "Das sind Widersprüche, kein Kalibrierfehler — kein Optimierungslauf kann sie auffangen. " +
                          "Prüfe die eingetragenen Positionen oder nimm das Paar heraus. Vollständige Liste in der " +
                          "Tabelle der Ausreißer."
            });
        }

        if (result.Near.Pairs >= 3 && result.Far.Pairs >= 3 &&
            result.Near.MedianAbsRssiErrorDb is { } nearErr && result.Far.MedianAbsRssiErrorDb is { } farErr &&
            farErr > nearErr * 2 && farErr - nearErr >= 5)
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                Category = "range",
                Message = $"Der Fit trägt in der Nähe und bricht mit der Entfernung zusammen: Median-Fehler {nearErr:0.0} dB " +
                          $"unterhalb von {NearFarSplitM:0} m gegen {farErr:0.0} dB darüber. Das ist die Handschrift " +
                          "einer Absorption, die an nahen Paaren gefittet und dann zu steil hochgerechnet wurde — " +
                          "ferne Knoten melden daraufhin eine viel zu kurze Entfernung und ziehen die Position zu " +
                          "sich heran."
            });
    }

    private static PairErrorTracker.PairErrorSnapshot? Lookup(
        Dictionary<string, PairErrorTracker.PairErrorSnapshot> history, string a, string b) =>
        history.TryGetValue($"{a}\u0000{b}", out var x) ? x
        : history.TryGetValue($"{b}\u0000{a}", out var y) ? y : null;

    /// <summary>
    /// Turns "how long has this been observed" into the sentence that changes what the reader does.
    /// Kept vague on purpose below two days: a pair seen briefly may simply not have been seen enough.
    /// </summary>
    private static string Age(double? observedHours) => observedHours switch
    {
        null => "",
        < 48 => $" Only watched for {observedHours:0} hours so far, so give it a day before acting.",
        < 24 * 14 => $" It has read this way across {observedHours / 24:0} days of observation.",
        _ => $" It has read this way for the whole {observedHours / 24:0} days it has been watched - " +
             "that is a placement or hardware matter, not something a fit can absorb."
    };

    private static FitQuality Summarize(List<Sample> samples)
    {
        if (samples.Count == 0) return new FitQuality();
        var distErrors = samples.Select(s => s.DistanceErrorM).OrderBy(v => v).ToList();
        return new FitQuality
        {
            Pairs = samples.Count,
            RmseM = Math.Round(Math.Sqrt(samples.Average(s => s.DistanceErrorM * s.DistanceErrorM)), 2),
            MedianBiasM = Math.Round(Median(distErrors), 2),
            MedianAbsRssiErrorDb = Math.Round(Median(samples.Select(s => Math.Abs(s.RssiErrorDb)).OrderBy(v => v).ToList()), 1)
        };
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private readonly record struct Sample(double RssiErrorDb, double DistanceErrorM);
}
