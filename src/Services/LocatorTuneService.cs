using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Wizard block 3b: locator parameter tuning via walk-test replay. Walk test points provide ground
/// truth (known device position) AND realistic input noise (raw per-tick node distances - exactly
/// what the locators consume live). Each nadaraya_watson bandwidth/kernel candidate is replayed
/// over every tick of every point; scored on mean 2D position error (accuracy) plus the standard
/// deviation of the estimates while the beacon sat still (jitter - the room-flapping symptom).
///
/// ★ Korrektur 2026-07-27: bis dahin rief dieser Dienst <c>NadarayaWatsonMultilateralizer.Estimate</c>
/// direkt auf UND filterte die hörbaren Knoten vorher auf die WAHRE Etage des Walk-Punkts. Damit
/// bewertete er einen einzelnen Schätzer, dem die schwerste Teilaufgabe - die Etagenentscheidung -
/// schon geschenkt worden war, und meldete das Ergebnis, als sei es das System. Genau der Fehler,
/// den <see cref="LocatorSweepService"/> am Benchmark benannt hatte. Jetzt läuft jeder Kandidat
/// durch denselben <see cref="ScenarioReplay"/> wie Sweep und Benchmark: alle konfigurierten
/// Locators auf ALLEN Etagen im Wettbewerb, Sieger nach Konfidenz. Dadurch sind die Zahlen dieses
/// Panels mit denen der anderen vergleichbar - und fallen erwartungsgemäß schlechter aus als vorher,
/// weil vorher zu viel verraten war.
///
/// Honest limitation, unverändert: stationäres Rauschen (keine Gehbewegung), und die
/// Kalman-Glättung über den Locators wird nicht nachgespielt - ein Walk-Punkt steht still.
/// </summary>
public class LocatorTuneService(State state, WalkTestService walkTest, ConfigLoader configLoader, ScenarioReplay replay)
{
    public class Candidate
    {
        public string Key { get; set; } = "";
        public double Bandwidth { get; set; }
        public string Kernel { get; set; } = "gaussian";
        public string Label { get; set; } = "";
    }

    public class CandidateResult
    {
        public Candidate Candidate { get; set; } = new();
        public double MeanErrorM { get; set; }
        public double MeanJitterM { get; set; }
        public double Score { get; set; }
        public int Ticks { get; set; }
        public int Points { get; set; }
        public bool IsCurrent { get; set; }

        /// <summary>Median statt Mittel - robuster gegen einzelne Ausreisser und dieselbe Kennzahl,
        /// die der Locator-Sweep und der Benchmark ausweisen, damit die Panels vergleichbar sind.</summary>
        public double? MedianErrorM { get; set; }

        public double? RoomHitRate { get; set; }
        public double? FloorHitRate { get; set; }
    }

    public class RunResult
    {
        public string? Error { get; set; }
        public List<CandidateResult> Results { get; set; } = new();
        public string? Recommendation { get; set; }
        public int PointsUsed { get; set; }
        public int TicksUsed { get; set; }
        public DateTime? RanAt { get; set; }
    }

    /// <summary>Jitter weight in the combined score: error counts full, instability half.</summary>
    private const double JitterWeight = 0.5;
    private const int MinTicksPerPoint = 5;

    private RunResult _last = new();

    public RunResult Status() => _last;

    public RunResult Run()
    {
        var result = new RunResult { RanAt = DateTime.UtcNow };
        try
        {
            var points = walkTest.GetPoints().Where(p => p.Raw.Count > 0 && p.FloorId != null).ToList();
            if (points.Count == 0)
            {
                result.Error = "No walk test points with raw tick data - record at least one walk test first (points recorded before this feature have no raw data).";
                _last = result;
                return result;
            }

            var nwConfig = configLoader.Config?.Locators?.NadarayaWatson;
            var currentBandwidth = nwConfig?.Bandwidth ?? 0.5;
            var currentKernel = nwConfig?.Kernel ?? "gaussian";

            var candidates = new List<Candidate>();
            foreach (var bw in new[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
                candidates.Add(new Candidate { Key = $"gaussian:{bw}", Bandwidth = bw, Kernel = "gaussian", Label = $"gaussian, bandwidth {bw}" });
            candidates.Add(new Candidate { Key = "inverse", Bandwidth = 0, Kernel = "inverse_square", Label = "inverse-distance-squared (no bandwidth)" });
            // Ensure the currently configured combination is always in the list.
            if (candidates.All(c => !(c.Kernel == currentKernel && Math.Abs(c.Bandwidth - currentBandwidth) < 0.001)) && currentKernel == "gaussian")
                candidates.Add(new Candidate { Key = $"gaussian:{currentBandwidth}", Bandwidth = currentBandwidth, Kernel = "gaussian", Label = $"gaussian, bandwidth {currentBandwidth} (current)" });

            var results = new List<CandidateResult>();
            var totalTicks = 0;

            var floors = state.Floors.Values.Where(f => f.Id != null).ToList();
            var locators = replay.ConfiguredLocators();
            var contrastWeight = replay.ConfiguredContrastWeight();
            if (!locators.Contains("nadaraya_watson"))
            {
                result.Error = "nadaraya_watson is not enabled, so its bandwidth and kernel have no effect on " +
                               "the live result. Enable it under Locator Selection first, or tune what is actually running.";
                _last = result;
                return result;
            }

            foreach (var candidate in candidates)
            {
                var perTickErrors = new List<double>();
                var perPointJitters = new List<double>();
                var pointsUsed = 0;
                var roomHits = 0; var roomChecked = 0;
                var floorHits = 0; var floorChecked = 0;

                foreach (var point in points)
                {
                    var truth = new Point3D(point.X, point.Y, point.Z);
                    var truthFloor = floors.FirstOrDefault(f => string.Equals(f.Id, point.FloorId, StringComparison.OrdinalIgnoreCase));
                    var truthRoom = SpatialUtils.FindRoomContaining(truth, truthFloor);
                    var estimates = new List<Point3D>();

                    foreach (var tickGroup in point.Raw.GroupBy(r => r.T))
                    {
                        // KEIN Etagen-Vorfilter mehr: live kennt das System die Etage nicht, sie ist
                        // genau das, was entschieden werden muss. Vorher wurde sie hier verraten.
                        var heard = new List<(Node node, double dist, double? var)>();
                        foreach (var entry in tickGroup)
                        {
                            if (!state.Nodes.TryGetValue(entry.N, out var node) || !node.HasLocation) continue;
                            if (entry.D <= 0) continue;
                            heard.Add((node, entry.D, entry.V));
                        }

                        var winner = replay.BestScenario(heard, floors, new ScenarioReplay.Options
                        {
                            Locators = locators,
                            ContrastWeight = contrastWeight,
                            NadarayaWatsonBandwidth = candidate.Kernel == "gaussian" ? candidate.Bandwidth : null,
                            NadarayaWatsonKernel = candidate.Kernel
                        });
                        if (winner == null) continue;

                        estimates.Add(winner.Location);
                        perTickErrors.Add(ScenarioReplay.Error2D(winner.Location, truth));

                        floorChecked++;
                        if (string.Equals(winner.Floor?.Id, point.FloorId, StringComparison.OrdinalIgnoreCase)) floorHits++;
                        if (truthRoom != null)
                        {
                            roomChecked++;
                            if (winner.Room?.Id == truthRoom.Id) roomHits++;
                        }
                    }

                    if (estimates.Count < MinTicksPerPoint) continue;
                    pointsUsed++;

                    var cx = estimates.Average(e => e.X);
                    var cy = estimates.Average(e => e.Y);
                    var jitter = Math.Sqrt(estimates.Average(e => Math.Pow(e.X - cx, 2) + Math.Pow(e.Y - cy, 2)));
                    perPointJitters.Add(jitter);
                }

                if (perTickErrors.Count == 0) continue;
                totalTicks = Math.Max(totalTicks, perTickErrors.Count);

                var sorted = perTickErrors.OrderBy(e => e).ToList();
                var meanError = perTickErrors.Average();
                var meanJitter = perPointJitters.Count > 0 ? perPointJitters.Average() : 0;
                results.Add(new CandidateResult
                {
                    Candidate = candidate,
                    MeanErrorM = meanError,
                    MeanJitterM = meanJitter,
                    MedianErrorM = Math.Round(sorted[sorted.Count / 2], 2),
                    RoomHitRate = roomChecked > 0 ? Math.Round((double)roomHits / roomChecked, 3) : null,
                    FloorHitRate = floorChecked > 0 ? Math.Round((double)floorHits / floorChecked, 3) : null,
                    Score = meanError + JitterWeight * meanJitter,
                    Ticks = perTickErrors.Count,
                    Points = pointsUsed,
                    IsCurrent = candidate.Kernel == currentKernel &&
                                (currentKernel != "gaussian" || Math.Abs(candidate.Bandwidth - currentBandwidth) < 0.001)
                });
            }

            if (results.Count == 0)
            {
                result.Error = "No candidate produced estimates - are the walk points on floors with at least 3 nodes hearing the device?";
                _last = result;
                return result;
            }

            result.Results = results.OrderBy(r => r.Score).ToList();
            result.PointsUsed = points.Count;
            result.TicksUsed = totalTicks;

            var best = result.Results[0];
            var current = result.Results.FirstOrDefault(r => r.IsCurrent);
            if (current != null && best.Score >= current.Score - 0.02)
                result.Recommendation = "Current locator settings already perform best (or within noise of the best) on the recorded walk points.";
            else
                result.Recommendation = $"Best on walk-test replay: {best.Candidate.Label} - mean error {best.MeanErrorM:0.00}m, jitter {best.MeanJitterM:0.00}m" +
                                        (current != null ? $" (current: {current.MeanErrorM:0.00}m / {current.MeanJitterM:0.00}m)" : "") + ".";

            Log.Information("Locator tune: {Count} candidates over {Points} points, best={Best}",
                results.Count, points.Count, best.Candidate.Label);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Locator tune failed");
            result.Error = ex.Message;
        }

        _last = result;
        return result;
    }

    public async Task<(bool ok, string? error)> Apply(string candidateKey)
    {
        var r = _last.Results.FirstOrDefault(x => x.Candidate.Key == candidateKey);
        if (r == null) return (false, $"Unknown candidate '{candidateKey}' - run the locator tune first");

        var c = configLoader.Config;
        if (c == null) return (false, "Config not loaded");

        if (r.Candidate.Kernel == "gaussian")
        {
            c.Locators.NadarayaWatson.Kernel = "gaussian";
            c.Locators.NadarayaWatson.Bandwidth = r.Candidate.Bandwidth;
        }
        else
        {
            // Anything other than "gaussian" makes the locator fall back to inverse-distance-squared.
            c.Locators.NadarayaWatson.Kernel = "inverse_square";
        }
        await configLoader.SaveSectionAsync("locators", c.Locators);
        Log.Information("Locator tune applied: kernel={Kernel}, bandwidth={Bandwidth}", r.Candidate.Kernel, r.Candidate.Bandwidth);
        return (true, null);
    }
}
