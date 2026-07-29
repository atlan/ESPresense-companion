using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Legt einzelne Knotenmessungen von Walk-Punkten still, die aus sich heraus unbrauchbar
/// sind — und ZWAR NUR SOLCHE.
///
/// ★ Die Trennlinie, auf die es ankommt: automatisch laeuft nur, was NICHT das Modell zum
/// Massstab nimmt, das mit denselben Daten kalibriert wird. Wer Messungen wegwirft, weil sie
/// nicht zum Fit passen, loescht so lange Residuen, bis der Fit schoen aussieht — und bekommt
/// eine praechtig kalibrierte Anlage, die schlechter ortet, ohne dass es auffaellt, weil der
/// Massstab mitgeschrumpft ist.
///
/// Erlaubt sind damit: Regeln aus der Messung selbst (zu wenige Werte, physikalisch unmoeglicher
/// Pegel) UND der Vergleich von WIEDERHOLUNGSMESSUNGEN am selben Ort gegeneinander — das ist
/// Ausreissererkennung unter Wiederholungen, keine Selbstbestaetigung des Fits.
///
/// ⚠ Nicht erlaubt: eine ganze Aufnahme verwerfen, weil ihr MEDIAN abweicht. Dann ist nicht
/// bestimmbar, welche Seite recht hat; die Diagnose sagt dort, was sie nicht weiss, statt zu
/// raten. Eine Ruecktrage an den Benutzer waere dasselbe Raten, nur mit fremder Unterschrift.
///
/// ⚠ Stillgelegt wird je MESSUNG, nie je Punkt: ein einzelner unbrauchbarer Knotenwert
/// macht die zehn anderen Messungen desselben Punkts nicht falsch, und der Bestand ist zu
/// duenn, um Brauchbares mitzuwerfen.
///
/// ⚠ Nichts wird geloescht. Walk-Punkte sind zu Fuss erlaufene Bodenwahrheit, und die
/// Diagnose, die sie verurteilt, kann irren.
/// </summary>
public class WalkPointHygiene(WalkTestService walkTest)
{
    /// <summary>
    /// Unter so wenigen Messwerten ist der Median des Aggregats Zufall. Gemessen an dieser
    /// Anlage liegt der Median bei 40 Werten je Knotenmessung; unter 10 sind es 2,7 % des
    /// Bestands — also die Ausreisser, nicht der Normalfall.
    /// </summary>
    public const int MinSamples = 10;

    /// <summary>
    /// Wieviel STAERKER eine Messung sein darf, als der Referenzpegel auf dieser Entfernung
    /// ueberhaupt zulaesst. Absorption 0 ist die physikalische Untergrenze — ein Pegel, der
    /// mit der Entfernung WAECHST, kann nicht stimmen.
    ///
    /// 3 dB Spielraum, damit nicht das Rauschen selbst zuschlaegt: derselbe Betrag, den
    /// WizardDiagnostics.MinExplainableDb schon als das ansetzt, was Koerper, Tueren und
    /// Geraeteorientierung ohnehin ausmachen. Am Bestand trifft das 3 von 372 Messungen.
    /// </summary>
    public const double MaxExcessDb = 3.0;

    /// <summary>
    /// ⚠ Sicherheitsabstand zur Singularitaet. Die geforderte Absorption ist
    /// (ref − rssi)/(10·log10 d) — bei d = 1 m ist der Nenner NULL, und jeder Messfehler
    /// explodiert. Am Bestand nachgemessen: im Band 0,9–1,3 m spannt die gerechnete
    /// Absorption von −161 bis +34, ausserhalb nur von −1,1 bis +7,3. Ohne diesen Abstand
    /// legt die Regel reihenweise voellig gesunde Nahmessungen stumm.
    /// </summary>
    public const double MinAbsLog10Distance = 0.3;   // d < 0,5 m oder d > 2,0 m

    /// <summary>
    /// Deckel gegen Kaskaden. Stilllegen aendert die Kalibrierung, die geaenderte
    /// Kalibrierung kann neue Auffaelligkeiten erzeugen, und ein Lauf, der beliebig viel
    /// wegnehmen darf, raeumt sich selbst leer. Die Regeln hier sind zwar
    /// modellunabhaengig und koennen das gar nicht ausloesen — der Deckel steht trotzdem,
    /// damit eine spaeter hinzugefuegte Regel keinen stillen Kahlschlag anrichten kann.
    /// </summary>
    public const double MaxShareOfFleet = 0.10;

    /// <summary>
    /// Unter so vielen aktiven Messungen kann ein Punkt gar nicht mehr geortet werden
    /// (ScenarioReplay.MinNodesPerTick). Ein Punkt, der dadurch unter die Grenze fiele,
    /// bleibt unangetastet — er ist dann als GANZES fragwuerdig, und das zu beurteilen ist
    /// nicht Aufgabe einer automatischen Regel.
    /// </summary>
    public const int MinActiveNodesPerPoint = 3;

    public record Befund(string PointId, string NodeId, string Rule, string Reason);

    /// <summary>
    /// Legt die Messung eines Knotens still, der in ZWEI Aufnahmen am selben Ort als einziger
    /// aus der Reihe faellt. Welche der beiden falsch liegt, ist nicht bestimmbar - der
    /// Widerspruch verschwindet nur, wenn beide Seiten schweigen.
    ///
    /// ⚠ Warum das trotz der Trennlinie oben automatisch laufen darf, obwohl das Kriterium
    /// aus der Diagnose stammt: verglichen werden WIEDERHOLUNGSMESSUNGEN am selben Ort
    /// gegeneinander, nicht Messungen gegen das Modell. Das ist Ausreissererkennung unter
    /// Wiederholungen, keine Selbstbestaetigung des Fits. Und es ist umkehrbar, protokolliert
    /// und gedeckelt - es gibt keinen Grund, davor stehenzubleiben und den Benutzer eine
    /// Muenze werfen zu lassen.
    ///
    /// ⚠ NUR der Einzelknoten-Fall. Weicht der MEDIAN ab, ist eine ganze Aufnahme fragwuerdig
    /// - dann sagt die Diagnose, was sie nicht weiss, statt zu raten.
    /// </summary>
    public List<Befund> ResolveSingleNodeConflicts(IEnumerable<(string IdA, string IdB, string NodeId, double MaxDb, double ExplainableDb)> faelle)
    {
        var punkte = walkTest.GetPoints();
        var gesamt = punkte.Sum(p => p.Nodes.Count);
        var schonAus = punkte.Sum(p => p.Nodes.Count(n => n.Disabled));
        var deckel = (int)Math.Floor(gesamt * MaxShareOfFleet);

        var getan = new List<Befund>();
        foreach (var f in faelle)
        {
            // ⚠ Der Deckel zaehlt ALLES Stillgelegte, nicht nur diesen Lauf. Sonst umgeht jede
            // zusaetzliche Regel ihn, indem sie ihr eigenes Kontingent bekommt - und die Summe
            // waechst unbegrenzt, obwohl jede einzelne Regel brav unter der Grenze bleibt.
            if (schonAus + getan.Count + 2 > deckel)
            {
                Log.Warning("Walk-Punkt-Hygiene: Deckel erreicht ({Aus} von {Gesamt} Messungen stillgelegt, " +
                            "Grenze {Cap}). Weitere Einzelknoten-Widersprueche bleiben stehen und sind in der " +
                            "Diagnose sichtbar - so viel Stilllegen auf einmal deutet auf ein gemeinsames " +
                            "Problem, nicht auf lauter Einzelfaelle.",
                            schonAus + getan.Count, gesamt, deckel);
                break;
            }

            var grund = $"Einzelner Knoten weicht um {f.MaxDb:0.0} dB ab, erklaerbar waeren {f.ExplainableDb:0.0} dB " +
                        $"(Doppel-Aufnahme {f.IdA}/{f.IdB} am selben Ort). Welche der beiden falsch liegt, ist nicht " +
                        "bestimmbar - deshalb schweigen beide.";
            foreach (var pid in new[] { f.IdA, f.IdB })
                if (SetDisabled(pid, f.NodeId, true, grund, "conflict-single-node"))
                    getan.Add(new Befund(pid, f.NodeId, "conflict-single-node", grund));
        }
        return getan;
    }

    /// <summary>
    /// Wendet die Regeln an. Gibt zurueck, was stillgelegt wurde — leer, wenn nichts.
    /// Bereits stillgelegte Messungen werden nicht angefasst (auch nicht reaktiviert:
    /// eine Reaktivierung ist immer eine bewusste Entscheidung).
    /// </summary>
    public List<Befund> Apply()
    {
        var punkte = walkTest.GetPoints();
        var gesamt = punkte.Sum(p => p.Nodes.Count);
        if (gesamt == 0) return [];

        var vorschlaege = new List<(WalkTestService.WalkTestPoint P, WalkTestService.NodeAggregate A, string Rule, string Reason)>();

        foreach (var p in punkte)
        foreach (var a in p.Nodes)
        {
            if (a.Disabled) continue;

            if (a.Samples < MinSamples)
            {
                vorschlaege.Add((p, a, "few-samples",
                    $"Nur {a.Samples} Messwerte (Mindestmass {MinSamples}) - der Median daraus ist Zufall."));
                continue;
            }

            var d = a.MapDistance;
            if (d <= 0 || Math.Abs(Math.Log10(d)) <= MinAbsLog10Distance) continue;

            // Positiv heisst: staerker gemessen, als die Referenz auf 1 m hergibt. Auf
            // groesserer Entfernung ist das unmoeglich - Absorption kann nicht negativ sein.
            var ueberschuss = a.MedianRssi - a.RefRssi;
            if (d > 1 && ueberschuss > MaxExcessDb)
                vorschlaege.Add((p, a, "impossible-level",
                    $"Aus {d:0.0} m um {ueberschuss:0.0} dB STAERKER gemessen als der Referenzpegel auf 1 m " +
                    $"({a.MedianRssi:0.0} gegen {a.RefRssi:0.0} dBm). Ein Pegel, der mit der Entfernung waechst, " +
                    "kann nicht stimmen - da hat etwas reflektiert oder der Knoten hat gesponnen."));
        }

        if (vorschlaege.Count == 0) return [];

        var deckel = (int)Math.Floor(gesamt * MaxShareOfFleet);
        if (vorschlaege.Count > deckel)
        {
            Log.Warning("Walk-Punkt-Hygiene: {Count} Messungen auffaellig, das ist mehr als der Deckel von " +
                        "{Cap} ({Share:P0} von {Total}). NICHTS stillgelegt - so viele Auffaelligkeiten auf " +
                        "einmal deuten auf ein gemeinsames Problem (verstellter Referenzpegel, falsches Geraet), " +
                        "nicht auf lauter Einzelfehler.",
                        vorschlaege.Count, deckel, MaxShareOfFleet, gesamt);
            return [];
        }

        var getan = new List<Befund>();
        foreach (var (p, a, rule, reason) in vorschlaege)
        {
            var aktiv = p.Nodes.Count(n => !n.Disabled);
            if (aktiv - 1 < MinActiveNodesPerPoint)
            {
                Log.Information("Walk-Punkt-Hygiene: {Point}/{Node} bliebe unter {Min} aktiven Messungen - " +
                                "unangetastet gelassen, der Punkt ist als Ganzes zu pruefen.",
                                p.Id, a.NodeId, MinActiveNodesPerPoint);
                continue;
            }
            a.Disabled = true;
            a.DisabledRule = rule;
            a.DisabledReason = reason;
            a.DisabledAt = DateTime.UtcNow;
            getan.Add(new Befund(p.Id, a.NodeId, rule, reason));
        }

        if (getan.Count > 0)
        {
            walkTest.PersistPoints();
            foreach (var b in getan)
                Log.Information("Walk-Punkt-Hygiene: {Point}/{Node} stillgelegt ({Rule}) - {Reason}",
                                b.PointId, b.NodeId, b.Rule, b.Reason);
        }
        return getan;
    }

    /// <summary>Eine Messung stilllegen oder wieder aufnehmen.</summary>
    public bool SetDisabled(string pointId, string nodeId, bool disabled, string? reason, string rule = "manual")
    {
        var p = walkTest.GetPoints().FirstOrDefault(x => string.Equals(x.Id, pointId, StringComparison.OrdinalIgnoreCase));
        var a = p?.Nodes.FirstOrDefault(x => string.Equals(x.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        if (a == null) return false;
        if (a.Disabled == disabled) return false;   // nichts zu tun, und nichts zu melden

        a.Disabled = disabled;
        a.DisabledRule = disabled ? rule : null;
        a.DisabledReason = disabled ? reason ?? "Von Hand stillgelegt." : null;
        a.DisabledAt = disabled ? DateTime.UtcNow : null;
        walkTest.PersistPoints();
        Log.Information("Walk-Punkt {Point}/{Node} {State} (von Hand){Reason}",
                        pointId, nodeId, disabled ? "stillgelegt" : "wieder aufgenommen",
                        disabled && reason != null ? $": {reason}" : "");
        return true;
    }
}
