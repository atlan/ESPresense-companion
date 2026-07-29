using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Legt einzelne Knotenmessungen von Walk-Punkten still, die aus sich heraus unbrauchbar
/// sind — und ZWAR NUR SOLCHE.
///
/// ★ Die Trennlinie, auf die es ankommt: hier stehen ausschliesslich Regeln, die OHNE das
/// Modell auskommen, das mit denselben Daten kalibriert wird. Wer Messungen wegwirft, weil
/// sie nicht zum Fit passen, loescht so lange Residuen, bis der Fit schoen aussieht — und
/// bekommt am Ende eine praechtig kalibrierte Anlage, die schlechter ortet, ohne dass es
/// auffaellt, weil der Massstab mitgeschrumpft ist. Solche Urteile (Mehrheitsentscheid
/// unter Wiederholungsaufnahmen, widerspruechliche Doppel-Aufnahmen) bleiben deshalb
/// VORSCHLAEGE in der Diagnose und werden nie automatisch vollzogen.
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

    /// <summary>Eine Messung von Hand stilllegen oder wieder aufnehmen.</summary>
    public bool SetDisabled(string pointId, string nodeId, bool disabled, string? reason)
    {
        var p = walkTest.GetPoints().FirstOrDefault(x => string.Equals(x.Id, pointId, StringComparison.OrdinalIgnoreCase));
        var a = p?.Nodes.FirstOrDefault(x => string.Equals(x.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        if (a == null) return false;

        a.Disabled = disabled;
        a.DisabledRule = disabled ? "manual" : null;
        a.DisabledReason = disabled ? reason ?? "Von Hand stillgelegt." : null;
        a.DisabledAt = disabled ? DateTime.UtcNow : null;
        walkTest.PersistPoints();
        Log.Information("Walk-Punkt {Point}/{Node} {State} (von Hand){Reason}",
                        pointId, nodeId, disabled ? "stillgelegt" : "wieder aufgenommen",
                        disabled && reason != null ? $": {reason}" : "");
        return true;
    }
}
