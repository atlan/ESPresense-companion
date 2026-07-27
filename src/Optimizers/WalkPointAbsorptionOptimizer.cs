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
/// bei 51,7 % Raumtrefferquote, waehrend ein flaches 2,5 auf 2,05 m und 61,8 % kommt. Beide Ziele
/// widersprechen einander, und das Paar-Ziel gewinnt per Konstruktion, weil es die Suche steuert.
/// Grenzen zu verengen half nicht - der Kandidat wird dann vom Komposit-Gate abgelehnt und die
/// alten, jetzt ausserhalb liegenden Werte bleiben stehen. Also muss das Ziel getauscht werden.
///
/// ★★ Gemeinsame Regression statt Median je Messung (Korrektur der ersten Fassung).
/// Die erste Version rechnete je Messung <c>a = (rssi@1m − rssi)/(10·log10(d))</c> und nahm den
/// Median. Das UNTERSTELLT einen korrekten Referenzpegel - und wenn der danebenliegt, ist die
/// abgeleitete Absorption verzerrt, wobei die Verzerrung MIT DER ENTFERNUNG WAECHST. Messungen aus
/// verschiedenen Distanzen liefern dann widerspruechliche Werte, und der Median verdeckt genau das,
/// was er zeigen muesste.
///
/// Richtig ist eine Regression im dB-Raum: <c>rssi = Ref − 10·A·log10(d)</c> ist eine GERADE ueber
/// log10(d). Steigung und Achsenabschnitt zusammen schaetzen heisst, Absorption und Referenzpegel
/// gemeinsam zu bestimmen, statt einen davon zu glauben.
///
/// Zwei Vorsichtsmassnahmen dabei:
/// - Vor dem Fit wird nach log10(d) GEBINNT und je Bin der Median genommen. Das macht die Steigung
///   robust gegen Ausreisser (ein Knoten hatte Pegelvarianz 42 gegen 0,2 bei den ruhigsten) und
///   verhindert, dass eine Distanz mit vielen Messungen die Gerade allein bestimmt.
/// - IDENTIFIZIERBARKEIT wird geprueft: ohne Spannweite in der Entfernung gibt es keine Steigung.
///   Ein Knoten, der das Geraet immer nur aus 5 m gesehen hat, bekommt keinen Wert - egal wie viele
///   Messungen. Genau deshalb braucht ein Walk-Test Punkte NAH, MITTEL und FERN je Knoten.
///
/// Bewertet wird anschliessend mit dem Referenzpegel des GERAETS, nicht mit dem selbst gefitteten
/// Achsenabschnitt: angewandt wird nur die Absorption, also muss sie sich auch unter den real
/// geltenden Bedingungen bewaehren. Der gefittete Achsenabschnitt bleibt als Diagnose im Log - weicht
/// er stark ab, stimmt etwas mit dem Referenzpegel oder der Empfindlichkeit dieses Knotens nicht.
///
/// ★ Zwei Sicherungen, weil auch eine saubere Regression sauber danebenliegen kann:
/// 1. HOLD-OUT je Knoten, geteilt nach ganzen Walk-PUNKTEN (Messungen desselben Punkts sind
///    Beinahe-Duplikate; sie ueber die Faltungen zu verteilen liesse den Wert besser aussehen als er
///    ist). Gemessen wird der Rest-Fehler in dB - nicht in Metern, weil ein Meter-Mass von den
///    fernen Messungen dominiert wird und Aenderungen im Nahbereich verschluckt.
/// 2. Das Walk-Punkt-Gate im Runner prueft danach Ende zu Ende. Der Hold-out sagt "dieser Exponent
///    beschreibt die Messung besser", das Gate sagt "und die Ortung wird dadurch besser" - zwei
///    verschiedene Fragen.
///
/// Knoten ohne ausreichende oder ohne genuegend GESPREIZTE Abdeckung bekommen keinen Vorschlag und
/// behalten den Wert aus dem Paar-Fit. Schweigen ist dort die ehrliche Antwort.
/// </summary>
public class WalkPointAbsorptionOptimizer(State state, WalkTestService walkTest, ConfigLoader configLoader) : IOptimizer
{
    public string Name => "Walk Point Absorption";

    /// <summary>Dieser Optimierer wird gegen die Bodenwahrheit bewertet, nicht gegen Knoten-Paare.</summary>
    public bool ScoredByWalkPoints => true;

    private const int MinSamplesPerNode = 20;

    /// <summary>Auf einem Meter faellt der Distanzterm weg, dort ist die Absorption unbestimmt.</summary>
    private const double MinAbsLogDistance = 0.05;

    /// <summary>Breite eines Entfernungs-Bins in log10(m) - 0,1 entspricht rund 26 % Distanzschritt.</summary>
    private const double BinWidthLog = 0.1;

    /// <summary>So viele verschiedene Bins muss ein Knoten haben, damit eine Steigung ueberhaupt bestimmbar ist.</summary>
    private const int MinBins = 3;

    /// <summary>...und so weit muessen sie auseinanderliegen (0,3 = Faktor 2 in der Entfernung).</summary>
    private const double MinLogSpan = 0.3;

    /// <summary>So viel muss der Hold-out-Rest-Fehler besser werden, damit es als Verbesserung zaehlt.</summary>
    private const double MinHoldoutGainDb = 0.3;

    private const int Folds = 3;

    private readonly record struct Sample(string NodeId, double LogD, double Rssi, double RefRssi);

    /// <summary>
    /// Knoten, deren Absorption aus Bodenwahrheit bestimmbar ist. Wer hier drin steht, GEHOERT
    /// diesem Optimierer - der Paar-Fit darf ihre Absorption nicht mehr anfassen.
    ///
    /// Ohne diese Eigentumsregel entsteht ein Tauziehen, live beobachtet am 27.07.2026: der
    /// Walk-Punkt-Fit senkt die Absorption, im naechsten Zyklus hebt der Paar-Fit sie zurueck (sein
    /// Komposit ist durch die Senkung schlechter geworden, sein Kandidat stellt es wieder her), und
    /// das wiederholt sich endlos. Beide Gates sagen jedes Mal ja, jeder hat fuer sich recht - nur
    /// passiert unterm Strich nichts.
    /// </summary>
    public IReadOnlySet<string> CoveredNodes()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Collect().SelectMany(p => p))
            counts[s.NodeId] = counts.TryGetValue(s.NodeId, out var n) ? n + 1 : 1;
        return counts.Where(kv => kv.Value >= MinSamplesPerNode)
                     .Select(kv => kv.Key)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Messungen je Walk-Punkt. Der Punkt traegt die Faltung, damit seine Messungen zusammenbleiben.</summary>
    private List<List<Sample>> Collect()
    {
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

                var d = node.Location.DistanceTo(truth);
                if (d <= 0) continue;
                var logD = Math.Log10(d);
                if (Math.Abs(logD) < MinAbsLogDistance) continue;

                samples.Add(new Sample(e.N, logD, rssi, refRssi));
            }

            if (samples.Count > 0) perPoint.Add(samples);
        }
        return perPoint;
    }

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();
        var opt = configLoader.Config?.Optimization;
        if (opt == null) return results;

        var perPoint = Collect();
        if (perPoint.Count < Folds)
        {
            Log.Debug("Walk point absorption: only {Count} usable points, need at least {Folds}", perPoint.Count, Folds);
            return results;
        }

        var byNode = perPoint.SelectMany((s, i) => s.Select(x => (fold: i % Folds, sample: x)))
                             .GroupBy(x => x.sample.NodeId, StringComparer.OrdinalIgnoreCase);

        int proposed = 0, tooFew = 0, notIdentifiable = 0, noGain = 0;
        foreach (var group in byNode)
        {
            var nodeId = group.Key;
            var all = group.ToList();
            if (all.Count < MinSamplesPerNode) { tooFew++; continue; }

            var full = Fit(all.Select(x => x.sample).ToList());
            if (full is not { } fit) { notIdentifiable++; continue; }

            var current = existingSettings.TryGetValue(nodeId, out var ns) ? ns.Calibration?.Absorption : null;
            if (current is null) { noGain++; continue; }

            // Hold-out: je Faltung auf dem Rest fitten, auf der zurueckgehaltenen Faltung pruefen -
            // und zwar mit dem Referenzpegel des GERAETS, denn nur die Absorption wird angewandt.
            double gain = 0;
            var folds = 0;
            for (var f = 0; f < Folds; f++)
            {
                var train = all.Where(x => x.fold != f).Select(x => x.sample).ToList();
                var test = all.Where(x => x.fold == f).Select(x => x.sample).ToList();
                if (train.Count < MinSamplesPerNode / 2 || test.Count == 0) continue;
                if (Fit(train) is not { } tf) continue;

                var candErr = MedianResidualDb(test, Clamp(tf.Absorption, opt));
                var baseErr = MedianResidualDb(test, Clamp(current.Value, opt));
                gain += baseErr - candErr;
                folds++;
            }

            if (folds == 0) { notIdentifiable++; continue; }
            gain /= folds;
            if (gain < MinHoldoutGainDb) { noGain++; continue; }

            var final = Clamp(fit.Absorption, opt);
            results.Nodes[nodeId] = new ProposedValues { Absorption = final };
            proposed++;

            // Der Achsenabschnitt wird NICHT geschrieben - er gehoert dem Geraet, nicht dem Knoten.
            // Als Diagnose ist er trotzdem wertvoll: weicht er vom gemeldeten rssi@1m ab, stimmt
            // etwas mit Referenzpegel oder Empfindlichkeit dieses Knotens nicht.
            var deviceRef = all[0].sample.RefRssi;
            Log.Information("Walk point absorption {Node}: {Current:0.00} -> {Final:0.00} " +
                            "(hold-out {Gain:0.00} dB besser, {N} Messungen, {Bins} Distanz-Bins; " +
                            "gefitteter Referenzpegel {FitRef:0.0} gegen gemeldete {DevRef:0.0} dBm)",
                nodeId, current, final, gain, all.Count, fit.Bins, fit.RefRssi, deviceRef);
        }

        Log.Information("Walk point absorption: {Proposed} vorgeschlagen | {NoGain} ohne Gewinn | " +
                        "{NotId} nicht bestimmbar (zu wenig Entfernungs-Spannweite) | {TooFew} zu wenige Messungen | aus {Points} Walk-Punkten",
            proposed, noGain, notIdentifiable, tooFew, perPoint.Count);
        return results;
    }

    private readonly record struct FitResult(double Absorption, double RefRssi, int Bins);

    /// <summary>
    /// Gerade durch die Bin-Mediane: rssi = Ref − 10·A·log10(d). Liefert null, wenn die Steigung
    /// mangels Entfernungs-Spannweite nicht bestimmbar ist.
    /// </summary>
    private static FitResult? Fit(IReadOnlyList<Sample> samples)
    {
        var bins = samples.GroupBy(s => Math.Round(s.LogD / BinWidthLog))
                          .Select(g => (x: g.Average(s => s.LogD), y: Median(g.Select(s => s.Rssi))))
                          .OrderBy(b => b.x)
                          .ToList();
        if (bins.Count < MinBins) return null;
        if (bins[^1].x - bins[0].x < MinLogSpan) return null;

        var n = bins.Count;
        var mx = bins.Average(b => b.x);
        var my = bins.Average(b => b.y);
        var sxx = bins.Sum(b => (b.x - mx) * (b.x - mx));
        if (sxx <= 1e-9) return null;
        var sxy = bins.Sum(b => (b.x - mx) * (b.y - my));

        var slope = sxy / sxx;              // = −10·A
        var absorption = -slope / 10.0;
        if (double.IsNaN(absorption) || absorption <= 0) return null;

        return new FitResult(absorption, my - slope * mx, n);
    }

    /// <summary>
    /// Medianer Betrag des Rest-Fehlers in dB, wenn dieser Exponent mit dem Referenzpegel des
    /// GERAETS benutzt wuerde. dB statt Meter, weil ein Meter-Mass von den fernen Messungen
    /// dominiert wird und Aenderungen im Nahbereich verschluckt.
    /// </summary>
    private static double MedianResidualDb(IReadOnlyList<Sample> samples, double absorption)
    {
        var errs = new List<double>(samples.Count);
        foreach (var s in samples)
            errs.Add(Math.Abs(s.Rssi - (s.RefRssi - 10.0 * absorption * s.LogD)));
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
