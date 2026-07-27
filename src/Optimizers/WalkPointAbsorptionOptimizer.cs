using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Optimizers;

/// <summary>
/// Fits per-node absorption against the WALK POINTS - the only ground truth in the system - instead
/// of against node-to-node agreement.
///
/// ★ Warum es das gibt. Der Knoten-Paar-Fit ist ein STELLVERTRETER. Er existiert, weil normalerweise
/// niemand weiss, wo das Geraet wirklich war, also laesst man die Knoten sich gegenseitig kalibrieren
/// und hofft, dass gute Uebereinstimmung auch gute Ortung bedeutet. Am 27.07.2026 wurde gemessen,
/// dass das hier NICHT gilt: der Paar-Fit liefert Absorptionen um 4,4 und ortet damit 2,40 m Median
/// bei 51,7 % Raumtrefferquote, waehrend ein flaches 2,5 auf 2,05 m und 61,8 % kommt. Die beiden
/// Ziele widersprechen einander, und das Paar-Ziel gewinnt per Konstruktion, weil es die Suche
/// steuert.
///
/// Grenzen zu verengen half nicht: der Kandidat wird dann vom Komposit-Gate abgelehnt und die alten,
/// jetzt ausserhalb liegenden Werte bleiben stehen. Deshalb muss das Ziel selbst getauscht werden.
///
/// ★ Kein Suchverfahren noetig. Fuer die Absorption liefern die Walk-Punkte eine GESCHLOSSENE Loesung
/// je Knoten: aus gemessenem Pegel und BEKANNTER Entfernung folgt direkt der Exponent, der beides in
/// Einklang bringt - <c>a = (rssi@1m - rssi) / (10 * log10(d_wahr))</c>. Median ueber alle Messungen
/// dieses Knotens, fertig. Das ist dieselbe Rechnung, die die Diagnose als "geforderte Absorption"
/// ausweist; sie war nur nie mit dem Fit verbunden.
///
/// ★ Zwei Sicherungen, weil eine geschlossene Loesung auch geschlossen danebenliegen kann:
/// 1. HOLD-OUT je Knoten. 32 Punkte gegen 18 Parameter ist wenig. Der Wert wird auf zwei Dritteln
///    der Punkte gebildet und auf dem zurueckgehaltenen Drittel geprueft; nur wenn er DORT besser
///    ist als der aktuelle, wird er vorgeschlagen. Geteilt wird nach ganzen Punkten, nicht nach
///    Messungen - Messungen desselben Punkts sind Beinahe-Duplikate, sie ueber die Faltung zu
///    verteilen liesse den Wert besser aussehen als er ist. Dasselbe Prinzip wie in AutoTuneService,
///    dort mit Knotenpaaren.
/// 2. Das Walk-Punkt-Gate im Runner prueft anschliessend die ganze Kette Ende zu Ende. Der
///    Hold-out sagt "dieser Exponent beschreibt die Messung besser", das Gate sagt "und die Ortung
///    wird dadurch tatsaechlich besser" - zwei verschiedene Fragen.
///
/// Knoten ohne ausreichende Walk-Punkt-Abdeckung bekommen KEINEN Vorschlag und behalten damit den
/// Wert aus dem Paar-Fit. Das ist Absicht: ein Knoten, den kein Walk-Punkt gehoert hat, ist hier
/// schlicht nicht messbar, und Schweigen ist die ehrliche Antwort.
/// </summary>
public class WalkPointAbsorptionOptimizer(State state, WalkTestService walkTest, ConfigLoader configLoader) : IOptimizer
{
    public string Name => "Walk Point Absorption";

    /// <summary>Dieser Optimierer wird gegen die Bodenwahrheit bewertet, nicht gegen Knoten-Paare.</summary>
    public bool ScoredByWalkPoints => true;

    /// <summary>Unter so vielen Messungen sagt ein Knoten-Median nichts.</summary>
    private const int MinSamplesPerNode = 20;

    /// <summary>Auf einem Meter faellt der Distanzterm weg, dort ist die Absorption unbestimmt.</summary>
    private const double MinAbsLogDistance = 0.05;

    /// <summary>So viel muss der Hold-out-Fehler besser werden, damit es als Verbesserung zaehlt.</summary>
    private const double MinHoldoutGainM = 0.05;

    private const int Folds = 3;

    private readonly record struct Sample(string NodeId, double Absorption, double TrueDist, double Rssi, double RefRssi);

    /// <summary>
    /// Knoten, deren Absorption aus Bodenwahrheit bestimmbar ist. Wer hier drin steht, GEHOERT
    /// diesem Optimierer - der Paar-Fit darf ihre Absorption nicht mehr anfassen.
    ///
    /// Ohne diese Eigentumsregel entsteht ein Tauziehen, live beobachtet am 27.07.2026: der
    /// Walk-Punkt-Fit senkt die Absorption, im naechsten Zyklus hebt der Paar-Fit sie zurueck (sein
    /// Komposit ist durch die Senkung schlechter geworden, also gewinnt sein Kandidat mit Sicherheit),
    /// und das Spiel wiederholt sich endlos. Beide Gates sagen jedes Mal ja, jeder fuer sich hat
    /// recht - nur passiert unterm Strich nichts.
    /// </summary>
    public IReadOnlySet<string> CoveredNodes()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in walkTest.GetPoints())
        {
            var truth = new Point3D(point.X, point.Y, point.Z);
            foreach (var e in point.Raw)
            {
                if (e.R is null || e.Ref is null) continue;
                if (!state.Nodes.TryGetValue(e.N, out var node) || !node.HasLocation) continue;
                var logD = Math.Log10(node.Location.DistanceTo(truth));
                if (Math.Abs(logD) < MinAbsLogDistance) continue;
                counts[e.N] = counts.TryGetValue(e.N, out var n) ? n + 1 : 1;
            }
        }
        return counts.Where(kv => kv.Value >= MinSamplesPerNode)
                     .Select(kv => kv.Key)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();
        var opt = configLoader.Config?.Optimization;
        if (opt == null) return results;

        // Messungen je Walk-Punkt sammeln. Der Punkt-Index traegt die Faltung, damit alle Messungen
        // eines Punkts zusammenbleiben.
        var perPoint = new List<List<Sample>>();
        foreach (var point in walkTest.GetPoints())
        {
            if (point.Raw.Count == 0) continue;
            var truth = new Point3D(point.X, point.Y, point.Z);
            var samples = new List<Sample>();

            foreach (var e in point.Raw)
            {
                if (e.R is not { } rssi || e.Ref is not { } refRssi) continue;
                if (!state.Nodes.TryGetValue(e.N, out var node) || !node.HasLocation) continue;

                var trueDist = node.Location.DistanceTo(truth);
                var logD = Math.Log10(trueDist);
                if (trueDist <= 0 || Math.Abs(logD) < MinAbsLogDistance) continue;

                var a = (refRssi - rssi) / (10.0 * logD);
                if (double.IsNaN(a) || double.IsInfinity(a)) continue;
                samples.Add(new Sample(e.N, a, trueDist, rssi, refRssi));
            }

            if (samples.Count > 0) perPoint.Add(samples);
        }

        if (perPoint.Count < Folds)
        {
            Log.Debug("Walk point absorption: only {Count} usable points, need at least {Folds}", perPoint.Count, Folds);
            return results;
        }

        var byNode = perPoint.SelectMany((s, i) => s.Select(x => (fold: i % Folds, sample: x)))
                             .GroupBy(x => x.sample.NodeId, StringComparer.OrdinalIgnoreCase);

        var proposed = 0;
        var rejected = 0;
        foreach (var group in byNode)
        {
            var nodeId = group.Key;
            var all = group.ToList();
            if (all.Count < MinSamplesPerNode) continue;

            var current = existingSettings.TryGetValue(nodeId, out var ns) ? ns.Calibration?.Absorption : null;

            // Hold-out: je Faltung auf dem Rest fitten, auf der Faltung pruefen.
            double gain = 0;
            var folds = 0;
            for (var f = 0; f < Folds; f++)
            {
                var train = all.Where(x => x.fold != f).Select(x => x.sample).ToList();
                var test = all.Where(x => x.fold == f).Select(x => x.sample).ToList();
                if (train.Count < MinSamplesPerNode / 2 || test.Count == 0) continue;

                var candidate = Clamp(Median(train.Select(s => s.Absorption)), opt);
                var candErr = MedianDistanceError(test, candidate);
                var baseErr = current is { } c ? MedianDistanceError(test, Clamp(c, opt)) : double.MaxValue;
                gain += baseErr - candErr;
                folds++;
            }

            if (folds == 0) continue;
            gain /= folds;

            if (gain < MinHoldoutGainM)
            {
                rejected++;
                continue;
            }

            var final = Clamp(Median(all.Select(x => x.sample.Absorption)), opt);
            results.Nodes[nodeId] = new ProposedValues { Absorption = final };
            proposed++;
            Log.Debug("Walk point absorption {Node}: {Current} -> {Final} (hold-out gain {Gain:0.00} m, {N} samples)",
                nodeId, current, final, gain, all.Count);
        }

        Log.Information("Walk point absorption: {Proposed} nodes proposed, {Rejected} rejected by hold-out, from {Points} walk points",
            proposed, rejected, perPoint.Count);
        return results;
    }

    /// <summary>Medianer Betrag der Distanzabweichung, wenn dieser Exponent benutzt wuerde.</summary>
    private static double MedianDistanceError(IReadOnlyList<Sample> samples, double absorption)
    {
        var errs = new List<double>(samples.Count);
        foreach (var s in samples)
        {
            var d = Math.Pow(10, (s.RefRssi - s.Rssi) / (10.0 * absorption));
            if (double.IsNaN(d) || double.IsInfinity(d)) continue;
            errs.Add(Math.Abs(d - s.TrueDist));
        }
        return errs.Count == 0 ? double.MaxValue : Median(errs);
    }

    private static double Clamp(double a, ConfigOptimization opt) =>
        Math.Clamp(a, opt.AbsorptionMin, opt.AbsorptionMax);

    private static double Median(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }
}
