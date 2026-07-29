using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Beantwortet die EINE Frage, die ein Benutzer an diese Seite hat: „Was muss ich tun, damit
/// es besser wird?"
///
/// ★ Warum das ein eigener Dienst ist und nicht noch eine Diagnose: Diagnosen beschreiben
/// ZUSTAENDE („12 Knoten weichen ab", „13 Raeume duenn abgedeckt"). Ein Zustand ist keine
/// Handlung. Der Benutzer musste bisher aus dreizehn Karten und zwanzig Knoepfen selbst
/// ableiten, was davon er beeinflussen kann - und die meisten Knoepfe starteten ohnehin nur
/// eine Rechnung, die auch von allein laufen kann.
///
/// ⚠ Hier steht deshalb NUR, was ein Mensch tun muss, weil es in der physischen Welt
/// passiert: irgendwo hingehen und messen, einen Knoten aufhaengen, den Beacon auf einen
/// Meter halten. Alles, was eine Rechnung ist, gehoert NICHT hierher - das laesst das System
/// selbst laufen und zeigt das Ergebnis.
///
/// ⚠ Sortiert nach NUTZEN, nicht nach Schwere. Ein dramatisch klingender Befund, an dem
/// niemand etwas aendern kann, steht hinter einer kleinen Aufraeumarbeit, die wirklich hilft.
/// </summary>
public class NextActions(
    State state,
    WalkTestService walkTest,
    WizardDiagnostics diagnostics,
    WalkPointPlanner planner,
    CalibrationBenchmark benchmark)
{
    /// <summary>
    /// Spiegel von WalkPointAbsorptionOptimizer: unterhalb dieser Spanne in log10(Entfernung)
    /// hat die Gerade keine bestimmbare Steigung, und damit ist die Absorption des Knotens
    /// nicht vom Referenzpegel trennbar. 0,3 entspricht Faktor 2 in der Entfernung.
    /// </summary>
    private const double MinLogSpan = 0.3;

    /// <summary>Weniger Aufnahmen als das, und die Spanne ist ohnehin nicht bestimmbar.</summary>
    private const int MinPointsPerNode = 3;

    /// <summary>Innerhalb dieses Radius mass diese Anlage 1,1 m Fehler, ausserhalb 2,5 m.</summary>
    private const double GoodCoverageM = 1.5;

    public NextActionsResult Compute()
    {
        var result = new NextActionsResult();

        var letzte = benchmark.Last;
        if (letzte != null && letzte.Error == null)
            result.Status = new SystemStatus
            {
                MedianErrorM = letzte.MedianErrorM,
                P90ErrorM = letzte.P90ErrorM,
                RoomHitRate = letzte.RoomHitRate,
                FloorHitRate = letzte.FloorHitRate,
                MeasuredAt = letzte.RanAt,
                Points = letzte.Points?.Count ?? 0
            };

        var diag = diagnostics.Analyze();

        SpannweiteFehlt(result);
        DuenneAbdeckung(result, diag);
        KnotenPruefen(result, diag);
        UmgehaengterKnoten(result, diag);
        Altlasten(result, diag);

        // Rang erst am Schluss, damit die einzelnen Pruefungen sich nicht um Zahlen streiten.
        result.Actions = result.Actions.OrderByDescending(a => a.Value).ToList();
        for (var i = 0; i < result.Actions.Count; i++) result.Actions[i].Rank = i + 1;
        return result;
    }

    /// <summary>
    /// Der groesste Hebel und der unsichtbarste: ein Knoten, von dem alle Walk-Punkte etwa
    /// gleich weit entfernt sind, laesst sich GAR NICHT kalibrieren. Das Modell ist
    /// rssi = ref − 10·A·log10(d); ohne Spannweite in d gibt es keine Steigung, und Absorption
    /// und Referenzpegel sind nicht voneinander trennbar. Mehr Punkte helfen nicht - andere
    /// Abstaende helfen.
    /// </summary>
    private void SpannweiteFehlt(NextActionsResult result)
    {
        var punkte = walkTest.GetPoints();
        var jeKnoten = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in punkte)
        foreach (var a in p.Nodes)
        {
            if (a.Disabled || a.MapDistance <= 0.1) continue;
            if (!jeKnoten.TryGetValue(a.NodeId, out var l)) jeKnoten[a.NodeId] = l = new List<double>();
            l.Add(a.MapDistance);
        }

        var betroffen = new List<NodeSpan>();
        foreach (var (id, dists) in jeKnoten)
        {
            if (dists.Count < MinPointsPerNode) continue;
            var min = dists.Min();
            var max = dists.Max();
            if (Math.Log10(max / min) >= MinLogSpan) continue;

            // Was fehlt: ein Punkt, der die Spanne auf Faktor 2 bringt. Lieber weiter weg als
            // naeher - im Nahbereich wird der Fit von der Singularitaet bei 1 m gestoert.
            var ziel = Math.Round(min * Math.Pow(10, MinLogSpan) * 1.2, 1);
            betroffen.Add(new NodeSpan
            {
                NodeId = id,
                NodeName = state.Nodes.TryGetValue(id, out var n) ? n.Name ?? id : id,
                Points = dists.Count,
                MinM = Math.Round(min, 1),
                MaxM = Math.Round(max, 1),
                SuggestedM = ziel
            });
        }
        if (betroffen.Count == 0) return;

        betroffen = betroffen.OrderByDescending(b => b.Points).ToList();
        result.Actions.Add(new NextAction
        {
            Id = "walk-span",
            Kind = ActionKind.Walk,
            // Nutzen: das ist der einzige Befund, der die Kalibrierung ganzer Knoten BLOCKIERT.
            // Alles andere macht sie schlechter, dieser macht sie unmoeglich.
            Value = 100 + betroffen.Count,
            Title = betroffen.Count == 1
                ? $"Einen Walk-Punkt in anderer Entfernung zu {betroffen[0].NodeName} aufnehmen"
                : $"Walk-Punkte in verschiedenen Entfernungen aufnehmen ({betroffen.Count} Knoten betroffen)",
            Why = $"{betroffen.Count} Knoten lassen sich derzeit gar nicht kalibrieren: alle vorhandenen " +
                  "Aufnahmen stehen ungefähr gleich weit von ihnen entfernt. Das Modell rechnet " +
                  "Pegel gegen den Logarithmus der Entfernung — ohne Spannweite gibt es keine Steigung, " +
                  "und die Dämpfung ist nicht vom Sendepegel zu trennen. Mehr Punkte helfen dabei " +
                  "nicht, nur andere Abstände.",
            Gain = "Schaltet die Kalibrierung dieser Knoten überhaupt erst frei.",
            NodeSpans = betroffen.Take(8).ToList(),
            Suggestions = planner.Suggest(3)
        });
    }

    private void DuenneAbdeckung(NextActionsResult result, WizardDiagnosticsResult diag)
    {
        var duenn = (diag.RoomCoverage ?? new())
            .Where(r => r.WellCoveredFraction < 0.5)
            .OrderByDescending(r => r.MedianNearestNodeM)
            .ToList();
        if (duenn.Count == 0) return;

        var schlimmster = duenn[0];
        result.Actions.Add(new NextAction
        {
            Id = "coverage",
            Kind = ActionKind.Hardware,
            // Hoher Nutzen, aber er kostet Geld und Arbeit - deshalb hinter dem Spaziergang,
            // der nichts kostet ausser zehn Minuten.
            Value = 60 + Math.Min(20, duenn.Count),
            Title = $"Einen zusätzlichen Knoten aufhängen — am dringendsten in {schlimmster.RoomName ?? schlimmster.RoomId}",
            Why = $"In {duenn.Count} von {(diag.RoomCoverage ?? new()).Count} Räumen liegt weniger als die Hälfte der " +
                  $"Fläche im Umkreis von {GoodCoverageM:0.0} m um einen Knoten. In " +
                  $"{schlimmster.RoomName ?? schlimmster.RoomId} sind es {schlimmster.WellCoveredFraction:P0}, " +
                  $"der nächste Knoten ist im Mittel {schlimmster.MedianNearestNodeM:0.0} m entfernt.",
            Gain = "An dieser Anlage gemessen: Stellen mit einem Knoten in Reichweite hatten 1,1 m Fehler, " +
                   "Stellen ohne 2,5 m. Mehr als jede Kalibrierung bewirken kann.",
            Rooms = duenn.Take(6).Select(r => $"{r.RoomName ?? r.RoomId} ({r.WellCoveredFraction:P0}, " +
                                              $"{r.MedianNearestNodeM:0.0} m)").ToList()
        });
    }

    /// <summary>
    /// Wenn EIN Knoten die Ausreisser-Liste beherrscht, ist er die Ursache und nicht
    /// zwanzig einzelne Paare. Das ist dann eine koerperliche Aufgabe: hinsehen, wo er haengt.
    /// </summary>
    private void KnotenPruefen(NextActionsResult result, WizardDiagnosticsResult diag)
    {
        var ausreisser = (diag.SignalOutliers ?? new()).Where(o => o.Reported).ToList();
        if (ausreisser.Count < 3) return;

        var gruppe = ausreisser.GroupBy(o => o.RxId).OrderByDescending(g => g.Count()).First();
        if (gruppe.Count() < 3 || gruppe.Count() * 2 < ausreisser.Count) return;

        var name = gruppe.First().RxName ?? gruppe.Key;
        result.Actions.Add(new NextAction
        {
            Id = "check-node",
            Kind = ActionKind.Hardware,
            Value = 50 + gruppe.Count(),
            Title = $"Nach „{name}“ sehen — er steckt in {gruppe.Count()} von {ausreisser.Count} Widersprüchen",
            Why = $"Dieser Knoten misst zu {gruppe.Count()} Nachbarn Pegel, die kein Pfadverlust erklärt " +
                  $"(bis {gruppe.Max(o => Math.Abs(o.DeltaDb)):0} dB daneben). Wenn ein einzelner Knoten " +
                  "die Liste beherrscht, liegt es an ihm und nicht an zwanzig Paaren.",
            Gain = "Prüfe die eingetragene Position, die Antenne und was um ihn herum steht " +
                   "(Metallschrank, Heizung, Kühlschrank).",
            Rooms = gruppe.Take(5).Select(o => $"→ {o.TxName ?? o.TxId}: {o.DeltaDb:+0;-0} dB").ToList()
        });
    }

    private void UmgehaengterKnoten(NextActionsResult result, WizardDiagnosticsResult diag)
    {
        var drift = diag.Issues.FirstOrDefault(i => i.Category == "geometry-drift");
        if (drift == null) return;

        result.Actions.Add(new NextAction
        {
            Id = "geometry-drift",
            Kind = ActionKind.Cleanup,
            Value = 30,
            Title = "Ein Knoten wurde versetzt — die alten Aufnahmen beschreiben ihn noch am alten Platz",
            Why = drift.Message,
            Gain = "Die betroffenen Messungen fließen bereits nicht mehr in die Kalibrierung ein. " +
                   "Sie neu aufzunehmen bringt die Bodenwahrheit an diesen Stellen zurück."
        });
    }

    private void Altlasten(NextActionsResult result, WizardDiagnosticsResult diag)
    {
        var ohne = (diag.StaleWalkPoints ?? new()).Count;
        if (ohne == 0) return;

        result.Actions.Add(new NextAction
        {
            Id = "stale-points",
            Kind = ActionKind.Cleanup,
            // Bewusst der kleinste Nutzen: es ist Aufraeumen, keine Verbesserung. Es steht hier
            // nur, damit die Liste nicht laenger wird, ohne dass jemand weiss warum.
            Value = 10,
            Title = $"{ohne} alte Walk-Punkte ohne Pegel aufräumen",
            Why = "Diese Aufnahmen stammen aus einer Zeit, in der die Pegel noch nicht mitgeschrieben " +
                  "wurden. Sie können den Locator prüfen, aber keine Kalibrierung bewerten.",
            Gain = "Kein direkter Gewinn an Genauigkeit — aber die Bewertungszahlen werden ehrlicher, " +
                   "weil sie nicht mehr über zwei verschiedene Datensorten mitteln."
        });
    }
}

public enum ActionKind
{
    /// <summary>Der Benutzer muss irgendwo hingehen und messen.</summary>
    Walk,
    /// <summary>Der Benutzer muss etwas anfassen: Knoten aufhaengen, umhaengen, nachsehen.</summary>
    Hardware,
    /// <summary>Aufraeumen in den Daten - kein Gewinn an Genauigkeit, aber ehrlichere Zahlen.</summary>
    Cleanup
}

public class NextAction
{
    public string Id { get; set; } = "";
    public int Rank { get; set; }
    /// <summary>Interner Nutzenwert zum Sortieren - nicht fuer die Anzeige gedacht.</summary>
    public int Value { get; set; }
    public ActionKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Why { get; set; } = "";
    public string? Gain { get; set; }
    public List<NodeSpan>? NodeSpans { get; set; }
    public List<string>? Rooms { get; set; }
    public List<WalkPointSuggestion>? Suggestions { get; set; }
}

public class NodeSpan
{
    public string NodeId { get; set; } = "";
    public string? NodeName { get; set; }
    public int Points { get; set; }
    public double MinM { get; set; }
    public double MaxM { get; set; }
    /// <summary>Entfernung, in der ein zusaetzlicher Punkt die Spanne ausreichend aufspannt.</summary>
    public double SuggestedM { get; set; }
}

public class SystemStatus
{
    public double? MedianErrorM { get; set; }
    public double? P90ErrorM { get; set; }
    public double? RoomHitRate { get; set; }
    public double? FloorHitRate { get; set; }
    public DateTime? MeasuredAt { get; set; }
    public int Points { get; set; }
}

public class NextActionsResult
{
    public SystemStatus? Status { get; set; }
    public List<NextAction> Actions { get; set; } = new();
}
