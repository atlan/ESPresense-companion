namespace ESPresense.Services;

/// <summary>
/// Wie sicher ist eine an Walk-Punkten gemessene Zahl — und trennt sie zwei Kandidaten
/// ueberhaupt?
///
/// ★★★ Warum das EINE Datei ist und nicht in jedem Dienst nochmal steht: am 29.07.2026 gab es
/// dieselbe Frage viermal an verschiedenen Stellen und dreimal verschieden beantwortet.
/// `LocatorSweepService` rechnete die Streuung ueber die Punkte, `LocatorTuneService` nahm eine
/// feste Schwelle von 0,02, `NextActions` bildete die Identifizierbarkeit des Fits nach (5
/// statt 9 Knoten). Jedes Mal sah die Nachbildung plausibel aus und war anders. Wer den
/// Benutzer auf eine Zahl hin handeln laesst, muss sie an EINER Stelle rechnen.
///
/// ⚠ Die Stichprobe sind die PUNKTE, nie die Ticks. Tausend Ticks klingen nach viel, aber die
/// Ticks innerhalb eines Punktes sind dasselbe Geraet, das an derselben Stelle steht - sie
/// sagen etwas ueber das Rauschen des Funks, nichts darueber, wie sich die Anlage anderswo
/// verhaelt. Der echte Stichprobenumfang ist, an wie vielen Stellen gemessen wurde.
/// Ueber Ticks gerechnet kaeme eine dreissigfach zu kleine Unsicherheit heraus, und jede
/// beliebige Schwankung sähe nach einem echten Unterschied aus.
/// </summary>
public static class PointUncertainty
{
    /// <summary>
    /// Standardfehler ueber die Walk-Punkte, oder null bei zu wenigen, um etwas zu sagen.
    /// </summary>
    public static double? StandardError(IReadOnlyList<double> perPoint)
    {
        if (perPoint.Count < 2) return null;
        var mean = perPoint.Average();
        var variance = perPoint.Sum(v => (v - mean) * (v - mean)) / (perPoint.Count - 1);
        return Math.Round(Math.Sqrt(variance / perPoint.Count), 3);
    }

    /// <summary>
    /// Alle Kandidaten, die vom Besten nicht weiter entfernt sind als dessen eigener
    /// Standardfehler - also die, zwischen denen die Messung NICHT unterscheidet.
    /// „Groesser ist besser"; fuer Fehlermasse den Wert negieren.
    ///
    /// Kommt mehr als einer zurueck, darf diese Groesse nicht entscheiden.
    /// </summary>
    public static List<T> WithinNoise<T>(IEnumerable<T> candidates, Func<T, double> value, Func<T, double?> error)
    {
        var list = candidates.ToList();
        if (list.Count == 0) return list;
        var leader = list.MaxBy(value)!;
        var margin = error(leader) ?? 0;
        return list.Where(c => value(leader) - value(c) <= margin).ToList();
    }

    /// <summary>
    /// Schlaegt der Kandidat den Ist-Zustand um MEHR als die Messunsicherheit?
    ///
    /// Das ist die Frage, an der eine automatische Umstellung haengt. Lautet die Antwort nein,
    /// waere ein Wechsel kein Fortschritt, sondern Unruhe - und ein Knopf, der ihn anbietet,
    /// laedt zu einer Entscheidung ein, die die Daten nicht hergeben.
    /// </summary>
    public static bool BeatsMeasurably(double candidate, double current, double? standardError) =>
        standardError is { } se && candidate - current > se;
}
