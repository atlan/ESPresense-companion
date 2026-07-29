using System.Text.Json;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Übernimmt Einstellungen, die MESSBAR besser sind, von selbst — statt dem Benutzer einen
/// „Apply"-Knopf hinzustellen, dessen Entscheidung er nicht treffen kann.
///
/// ★ Warum ein Knopf hier falsch war: die Wahl zwischen „nadaraya_watson + mle" und
/// „nelder_mead alone" ist eine reine Messfrage. Der Benutzer bringt nichts ein, was das
/// System nicht schon hat — er kann nur die oberste Zeile einer Rangliste anklicken und
/// hoffen. Eine Entscheidung ohne Grundlage abzufragen verschiebt keine Verantwortung,
/// sondern versteckt einen unfertigen Analyseschritt.
///
/// ⚠ Aber NUR, wenn der Unterschied groesser ist als die Messunsicherheit. `LocatorSweep`
/// rechnet sie ueber die Walk-PUNKTE (nicht ueber Ticks - tausend Ticks an einem Ort sind
/// eine Stichprobe, nicht tausend) und legt in `DecidedBy` offen, WAS entschieden hat.
/// Steht dort „simplicity", trennt die Messung die Kandidaten nicht: dann wird nichts
/// umgestellt. Zwei Einstellungen, die sich statistisch nicht unterscheiden, gegeneinander
/// zu tauschen ist keine Verbesserung, sondern Unruhe.
///
/// ⚠ Und immer sichtbar und ruecknehmbar. Das hier aendert, was das HAUS tut - anders als
/// eine stillgelegte Messung, die nur intern wirkt. Der Benutzer muss erfahren, dass sich
/// das Verhalten geaendert hat, und es mit einem Klick zurueckdrehen koennen.
/// </summary>
public class AutoApply(LocatorSweepService sweep, LocatorTuneService locatorTune, ConfigLoader configLoader, string? persistPath = null)
{
    /// <summary>
    /// Was „messbar" heisst: alles ausser „simplicity". Die drei anderen Werte bedeuten, dass
    /// der Vorsprung des Gewinners groesser war als die eigene Streuung der Messung.
    /// </summary>
    private static readonly string[] MessbarEntschieden = ["room", "floor", "position"];

    private readonly object _lock = new();
    private List<AppliedChange> _history = Load(persistPath);

    public IReadOnlyList<AppliedChange> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    /// <summary>
    /// Prueft den Locator-Sweep und stellt um, wenn es sich messbar lohnt. Gibt zurueck, was
    /// getan wurde - null, wenn nichts.
    /// </summary>
    public async Task<AppliedChange?> RunLocatorChoice(DateTime now)
    {
        // Frisch rechnen: die Empfehlung soll den heutigen Zustand bewerten, nicht einen
        // Sweep von vorgestern. Der Lauf kostet Sekunden und findet nur alle paar Stunden statt.
        var rec = sweep.Run()?.Recommendation;
        if (rec == null) return null;

        if (rec.AlreadyConfigured) return null;
        if (!MessbarEntschieden.Contains(rec.DecidedBy, StringComparer.OrdinalIgnoreCase))
        {
            Log.Debug("Locator-Wahl: nichts umgestellt - die Messung trennt die Kandidaten nicht ({Why})", rec.DecidedBy);
            return null;
        }

        var c = configLoader.Config;
        if (c == null) return null;

        var vorher = new List<string>();
        if (c.Locators.NadarayaWatson.Enabled) vorher.Add("nadaraya_watson");
        if (c.Locators.NelderMead.Enabled) vorher.Add("nelder_mead");
        if (c.Locators.Mle.Enabled) vorher.Add("mle");
        if (c.Locators.Bfgs.Enabled) vorher.Add("bfgs");
        if (c.Locators.NearestNode.Enabled) vorher.Add("nearest_node");

        await Setzen(rec.Locators);

        var change = new AppliedChange
        {
            At = now,
            Kind = "locators",
            Title = $"Ortungsverfahren auf „{rec.Label}“ umgestellt",
            Reason = rec.Reason,
            Before = vorher,
            After = rec.Locators.ToList(),
            RoomHitRate = rec.RoomHitRate,
            MedianErrorM = rec.MedianErrorM,
            FloorHitRate = rec.FloorHitRate
        };
        lock (_lock)
        {
            _history.Insert(0, change);
            if (_history.Count > 20) _history = _history.Take(20).ToList();
            Save();
        }
        Log.Information("Automatisch umgestellt: {Title}. {Reason}", change.Title, change.Reason);
        return change;
    }

    /// <summary>
    /// Bandbreite und Kernel von Nadaraya-Watson: dieselbe Frage eine Ebene tiefer. Auch hier
    /// gilt, dass nur umgestellt wird, was den Ist-Zustand um MEHR als die Streuung der Messung
    /// schlaegt - `LocatorTuneService.BeatsCurrentMeasurably` rechnet sie seit dem 29.07. ueber
    /// die Walk-PUNKTE statt gegen eine feste Schwelle 0,02, die aus nichts folgte.
    /// </summary>
    public async Task<AppliedChange?> RunLocatorTuning(DateTime now)
    {
        var res = locatorTune.Run();
        if (res?.Error != null || res?.Results is not { Count: > 0 }) return null;
        if (!res.BeatsCurrentMeasurably) return null;

        var best = res.Results[0];
        var current = res.Results.FirstOrDefault(r => r.IsCurrent);
        var (ok, error) = await locatorTune.Apply(best.Candidate.Key);
        if (!ok)
        {
            Log.Warning("Locator-Feineinstellung nicht uebernommen: {Error}", error);
            return null;
        }

        var change = new AppliedChange
        {
            At = now,
            Kind = "locator-tuning",
            Title = $"Ortungs-Feineinstellung auf „{best.Candidate.Label}“ umgestellt",
            Reason = res.Recommendation ?? "",
            Before = current != null ? [current.Candidate.Label] : [],
            After = [best.Candidate.Label],
            MedianErrorM = best.MedianErrorM
        };
        lock (_lock)
        {
            _history.Insert(0, change);
            if (_history.Count > 20) _history = _history.Take(20).ToList();
            Save();
        }
        Log.Information("Automatisch umgestellt: {Title}. {Reason}", change.Title, change.Reason);
        return change;
    }

    /// <summary>Eine automatische Umstellung zuruecknehmen.</summary>
    public async Task<bool> Undo(string id)
    {
        AppliedChange? change;
        lock (_lock) change = _history.FirstOrDefault(h => h.Id == id && !h.UndoneAt.HasValue);
        if (change == null || change.Before.Count == 0) return false;

        if (change.Kind == "locators") await Setzen(change.Before);
        else if (change.Kind == "locator-tuning")
        {
            // Die Feineinstellung wird ueber ihren Kandidaten-Schluessel zurueckgesetzt; steckt der
            // alte Zustand nicht mehr in der Kandidatenliste, ist keine saubere Rueckkehr moeglich.
            var zurueck = locatorTune.Run()?.Results
                .FirstOrDefault(r => r.Candidate.Label == change.Before[0]);
            if (zurueck == null) return false;
            var (ok, _) = await locatorTune.Apply(zurueck.Candidate.Key);
            if (!ok) return false;
        }
        else return false;
        lock (_lock)
        {
            change.UndoneAt = DateTime.UtcNow;
            Save();
        }
        Log.Information("Automatische Umstellung zurueckgenommen: {Title}", change.Title);
        return true;
    }

    private async Task Setzen(List<string> locators)
    {
        var c = configLoader.Config;
        if (c == null) return;
        bool An(string id) => locators.Contains(id, StringComparer.OrdinalIgnoreCase);
        c.Locators.NadarayaWatson.Enabled = An("nadaraya_watson");
        c.Locators.NelderMead.Enabled = An("nelder_mead");
        c.Locators.Mle.Enabled = An("mle");
        c.Locators.Bfgs.Enabled = An("bfgs");
        c.Locators.NearestNode.Enabled = An("nearest_node");
        await configLoader.SaveSectionAsync("locators", c.Locators);
    }

    private void Save()
    {
        if (persistPath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(persistPath)!);
            File.WriteAllText(persistPath, JsonSerializer.Serialize(_history));
        }
        catch (Exception ex) { Log.Warning(ex, "Verlauf der automatischen Umstellungen nicht gespeichert"); }
    }

    private static List<AppliedChange> Load(string? path)
    {
        if (path == null || !File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<AppliedChange>>(File.ReadAllText(path)) ?? []; }
        catch (Exception ex) { Log.Warning(ex, "Verlauf der automatischen Umstellungen nicht gelesen"); return []; }
    }
}

public class AppliedChange
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime At { get; set; }
    public DateTime? UndoneAt { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> Before { get; set; } = new();
    public List<string> After { get; set; } = new();
    public double? RoomHitRate { get; set; }
    public double? MedianErrorM { get; set; }
    public double? FloorHitRate { get; set; }
}
