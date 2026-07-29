using ESPresense.Models;
using ESPresense.Optimizers;

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
///
/// ★★★ HARTE REGEL, an die ich mich beim ersten Anlauf selbst nicht gehalten habe:
/// **Ein Eintrag gehoert nur hierher, wenn er VERSCHWINDET, sobald man ihn erledigt.**
///
/// Die erste Fassung fuehrte zwei „Aufraeumen"-Punkte, die das nicht taten:
///   • „Knoten wurde versetzt, Aufnahmen neu machen" — ein neuer Spaziergang legt einen
///     ZUSAETZLICHEN Punkt an, der alte bleibt stehen (genau die Falle, die am selben Tag
///     schon in der Nachhol-Tabelle steckte). Die Meldung sagt ausserdem selbst, dass der
///     Fit korrekt bleibt: es war nie eine Handlung, sondern eine Information.
///   • „22 Punkte ohne Pegel aufraeumen" — der Benchmark trennt die beiden Datensorten
///     laengst und meldet beide Zahlen (33 Punkte: 1,98 m / 57,6 % · 11 mit Pegeln:
///     1,55 m / 48,2 %). Es gab nichts zu verbessern, und Loeschen haette 22 von 33
///     Punkten Bodenwahrheit fuer den Locator vernichtet.
///
/// Ein Eintrag, der nach dem Erledigen stehenbleibt, bringt dem Benutzer bei, die ganze
/// Liste zu ueberblaettern. Beides gehoert in die Diagnose unter „Details", nicht hierher.
/// </summary>
public class NextActions(
    State state,
    WalkTestService walkTest,
    WizardDiagnostics diagnostics,
    WalkPointPlanner planner,
    CalibrationBenchmark benchmark,
    ConfigLoader configLoader)
{
    /// <summary>Innerhalb dieses Radius mass diese Anlage 1,1 m Fehler, ausserhalb 2,5 m.</summary>
    private const double GoodCoverageM = 1.5;

    public NextActionsResult Compute()
    {
        var result = new NextActionsResult();

        // ⚠ NICHT einfach benchmark.Last nehmen. Das ist der zuletzt GEMERKTE Lauf - und der
        // kann von Hand mit anderen Einstellungen angestossen worden sein (etwa bewusst gegen
        // den Mitschnitt). Genau so zeigte die Statuszeile 2,38 m statt der tatsaechlichen
        // 1,98 m. Hier wird deshalb frisch gerechnet, mit der heutigen Kalibrierung; ein Lauf
        // kostet unter einer Sekunde und wird nicht in den Verlauf geschrieben.
        BenchmarkResult? letzte = null;
        try { letzte = benchmark.Run(label: "status", overrides: benchmark.CurrentCalibrationOverrides(), remember: false); }
        catch { /* ohne Walk-Punkte gibt es keinen Status - die Handlungen stehen trotzdem */ }

        if (letzte != null && letzte.Error == null)
            result.Status = new SystemStatus
            {
                MedianErrorM = letzte.MedianErrorM,
                P90ErrorM = letzte.P90ErrorM,
                RoomHitRate = letzte.RoomHitRate,
                FloorHitRate = letzte.FloorHitRate,
                MeasuredAt = letzte.RanAt,
                Points = letzte.Points?.Count ?? 0,
                PointsWithLevels = letzte.PointsWithLevels,
                // Was die Kalibrierung ueberhaupt bewerten kann: nur Punkte MIT Pegeln reagieren
                // auf eine Kalibrieraenderung. Das ist KEINE Handlung fuer den Benutzer - der
                // Benchmark rechnet beide Zahlen ohnehin getrennt. Es gehoert nur dazugesagt,
                // damit niemand die grosse Zahl fuer die Kalibrierguete haelt.
                ResponsiveMedianErrorM = letzte.ResponsiveMedianErrorM,
                ResponsiveRoomHitRate = letzte.ResponsiveRoomHitRate
            };

        var diag = diagnostics.Analyze();

        SpannweiteFehlt(result);
        DuenneAbdeckung(result, diag);
        KnotenPruefen(result, diag);

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
    ///
    /// ⚠ Die Frage wird dem FIT gestellt, nicht nachgebildet. Eine naheliegende Nachbildung
    /// ueber die blosse Entfernungsspanne kam am 29.07.2026 auf 5 Knoten, der Fit selbst auf 9 -
    /// wer den Benutzer losschickt, muss ihm den echten Grund nennen.
    /// </summary>
    private void SpannweiteFehlt(NextActionsResult result)
    {
        var status = new WalkPointAbsorptionOptimizer(state, walkTest, configLoader).Diagnose();
        var betroffen = status
            .Where(s => s.Reason is FitBlocker.NoDistanceSpan or FitBlocker.NoDistanceSpanInFolds)
            .OrderByDescending(s => s.Samples)
            .Select(s => new NodeSpan
            {
                NodeId = s.NodeId,
                NodeName = state.Nodes.TryGetValue(s.NodeId, out var n) ? n.Name ?? s.NodeId : s.NodeId,
                Points = s.Samples,
                MinM = s.MinDistanceM,
                MaxM = s.MaxDistanceM,
                SuggestedM = Vorschlag(s),
                OnlyInFolds = s.Reason == FitBlocker.NoDistanceSpanInFolds
            })
            .ToList();
        if (betroffen.Count == 0) return;

        var knapp = betroffen.Count(b => b.OnlyInFolds);
        result.Actions.Add(new NextAction
        {
            Id = "walk-span",
            Kind = ActionKind.Walk,
            // Der einzige Befund, der die Kalibrierung ganzer Knoten BLOCKIERT. Alles andere
            // macht sie schlechter, dieser macht sie unmoeglich - deshalb immer obenauf.
            Value = 100 + betroffen.Count,
            Title = betroffen.Count == 1
                ? $"Einen Walk-Punkt in anderer Entfernung zu {betroffen[0].NodeName} aufnehmen"
                : $"Walk-Punkte in verschiedenen Entfernungen aufnehmen — {betroffen.Count} Knoten betroffen",
            Why = $"{betroffen.Count} Knoten lassen sich derzeit nicht kalibrieren: alle Aufnahmen, die " +
                  "Pegel tragen, stehen ungefähr gleich weit von ihnen entfernt. Das Modell rechnet " +
                  "Pegel gegen den Logarithmus der Entfernung — ohne Spannweite gibt es keine Steigung, " +
                  "und die Dämpfung ist nicht vom Sendepegel zu trennen. Mehr Punkte helfen dabei " +
                  "nicht, nur andere Abstände." +
                  (knapp > 0
                      ? $" Bei {knapp} davon reicht es knapp nicht mehr, sobald zum Prüfen ein Teil " +
                        "zurückgehalten wird — ungeprüft wird nichts angewandt."
                      : ""),
            Gain = "Schaltet die Kalibrierung dieser Knoten überhaupt erst frei. Ein einziger Punkt " +
                   "in der genannten Entfernung genügt je Knoten.",
            NodeSpans = betroffen.Take(8).ToList(),
            Suggestions = planner.Suggest(3)
        });
    }

    /// <summary>Naeher heran, so nah es sinnvoll ist.</summary>
    private const double NahzielM = 1.4;

    /// <summary>Darueber hinaus traegt eine Messung nichts mehr bei (siehe WalkPointPlanner).</summary>
    private const double MaxNutzbarM = 12.0;

    /// <summary>
    /// In welcher Entfernung ein zusaetzlicher Punkt diesem Knoten hilft.
    ///
    /// ⚠ NAEHER, nicht weiter. Der erste Anlauf schlug „weiter weg" vor und kam bei einem Knoten
    /// mit 3,5–10 m Spanne auf **16 m** heraus - eine Entfernung, die es im Haus nicht gibt.
    /// Ein Rat, den man nicht befolgen kann, ist schlimmer als keiner. Naeher herangehen ist
    /// dagegen immer moeglich: man stellt sich neben den Knoten.
    ///
    /// ⚠ Aber nicht bis auf einen Meter. Dort faellt der Distanzterm weg (log10(1) = 0) und die
    /// Absorption ist prinzipiell nicht bestimmbar - genau die Singularitaet, an der sich heute
    /// schon die Hygiene-Regel die Zaehne ausgebissen hat.
    ///
    /// Bei der knappen Variante (Spanne reicht nur ohne Hold-out) fehlt keine SPANNE, sondern
    /// eine zweite Stuetze am Rand: dort hilft ein Punkt in der Naehe des naechstgelegenen.
    /// </summary>
    private static double Vorschlag(NodeFitStatus s)
    {
        if (s.Reason == FitBlocker.NoDistanceSpanInFolds)
            return Math.Round(Math.Max(NahzielM, s.MinDistanceM * 0.9), 1);

        if (s.MinDistanceM / 2.2 >= NahzielM) return Math.Round(s.MinDistanceM / 2.2, 1);
        if (s.MaxDistanceM * 2.2 <= MaxNutzbarM) return Math.Round(s.MaxDistanceM * 2.2, 1);
        return NahzielM;
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

}

public enum ActionKind
{
    /// <summary>Der Benutzer muss irgendwo hingehen und messen.</summary>
    Walk,
    /// <summary>Der Benutzer muss etwas anfassen: Knoten aufhaengen, umhaengen, nachsehen.</summary>
    Hardware
    // Bewusst KEIN "Cleanup": Aufraeumen macht nichts besser, und ein Eintrag, der nach dem
    // Erledigen stehenbleibt, gehoert nicht in diese Liste. Siehe die Regel im Kopfkommentar.
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
    /// <summary>Insgesamt reicht die Spanne, nur beim Zurueckhalten zum Pruefen nicht mehr.</summary>
    public bool OnlyInFolds { get; set; }
}

public class SystemStatus
{
    public double? MedianErrorM { get; set; }
    public double? P90ErrorM { get; set; }
    public double? RoomHitRate { get; set; }
    public double? FloorHitRate { get; set; }
    public DateTime? MeasuredAt { get; set; }
    public int Points { get; set; }
    /// <summary>Wie viele davon Pegel tragen und damit auf Kalibrieraenderungen reagieren koennen.</summary>
    public int PointsWithLevels { get; set; }
    public double? ResponsiveMedianErrorM { get; set; }
    public double? ResponsiveRoomHitRate { get; set; }
}

public class NextActionsResult
{
    public SystemStatus? Status { get; set; }
    public List<NextAction> Actions { get; set; } = new();
}
