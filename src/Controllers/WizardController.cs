using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace ESPresense.Controllers;

/// <summary>
/// Endpoints backing the calibration setup wizard: config/geometry/placement validation,
/// node health gate, on-demand calibration trigger, and excluded-pairs suggestions.
/// </summary>
[ApiController]
public class WizardController(
    WizardService wizard,
    PairErrorTracker pairErrorTracker,
    OptimizationRunner optimizationRunner,
    ConfigLoader configLoader,
    State state,
    WalkTestService walkTest,
    AutoTuneService autoTune,
    LocatorTuneService locatorTune) : ControllerBase
{
    /// <summary>Renders a config pair id ("node_a:node_b") with friendly node names for display.</summary>
    private string FriendlyPair(string pair)
    {
        var parts = pair.Split(':', 2);
        if (parts.Length != 2) return pair;
        var a = parts[0].Trim();
        var b = parts[1].Trim();
        state.Nodes.TryGetValue(a, out var nodeA);
        state.Nodes.TryGetValue(b, out var nodeB);
        return $"{nodeA?.Name ?? a} ↔ {nodeB?.Name ?? b}";
    }

    [HttpGet("api/wizard/validation")]
    public WizardValidationResult GetValidation()
    {
        return wizard.Validate();
    }

    [HttpGet("api/wizard/health")]
    public HealthGateResult GetHealth()
    {
        return wizard.HealthGate();
    }

    [HttpPost("api/wizard/calibrate-now")]
    public IActionResult CalibrateNow()
    {
        var optimization = configLoader.Config?.Optimization;
        if (optimization is not { Enabled: true })
            return BadRequest(new { error = "Optimization is disabled - enable auto-optimization first." });

        optimizationRunner.TriggerNow();
        Log.Information("Calibration cycle manually triggered via wizard");
        return Ok(new { triggered = true });
    }

    [HttpGet("api/wizard/excluded-pairs/suggestions")]
    public IActionResult GetExcludedPairSuggestions()
    {
        var excluded = configLoader.Config?.Optimization?.ExcludedPairs;
        return Ok(new
        {
            suggestions = pairErrorTracker.GetSuggestions(excluded),
            currentlyExcluded = excluded ?? new List<string>(),
            currentlyExcludedFriendly = (excluded ?? new List<string>()).Select(FriendlyPair).ToList(),
            minSamples = PairErrorTracker.MinSamples,
            minObservationHours = PairErrorTracker.MinObservation.TotalHours,
            minAboveFraction = PairErrorTracker.MinAboveFraction,
            threshold = PairErrorTracker.SuggestionThreshold
        });
    }

    [HttpPost("api/wizard/excluded-pairs")]
    public async Task<IActionResult> AddExcludedPairs([FromBody] List<string> pairs)
    {
        try
        {
            var c = configLoader.Config;
            if (c == null) return StatusCode(500, new { error = "Config not loaded" });

            var valid = pairs
                .Where(p => p.Split(':', 2) is { Length: 2 } parts &&
                            !string.IsNullOrWhiteSpace(parts[0]) &&
                            !string.IsNullOrWhiteSpace(parts[1]))
                .ToList();
            if (valid.Count == 0)
                return BadRequest(new { error = "No valid 'node_a:node_b' pairs in request" });

            var added = new List<string>();
            foreach (var pair in valid)
            {
                var parts = pair.Split(':', 2);
                var exists = c.Optimization.ExcludedPairs.Any(e =>
                {
                    var ep = e.Split(':', 2);
                    if (ep.Length != 2) return false;
                    var a = ep[0].Trim();
                    var b = ep[1].Trim();
                    return (a.Equals(parts[0].Trim(), StringComparison.OrdinalIgnoreCase) && b.Equals(parts[1].Trim(), StringComparison.OrdinalIgnoreCase)) ||
                           (a.Equals(parts[1].Trim(), StringComparison.OrdinalIgnoreCase) && b.Equals(parts[0].Trim(), StringComparison.OrdinalIgnoreCase));
                });
                if (exists) continue;
                c.Optimization.ExcludedPairs.Add(pair.Trim());
                added.Add(pair.Trim());
            }

            if (added.Count > 0)
            {
                await configLoader.SaveSectionAsync("optimization", c.Optimization);
                Log.Information("Wizard added excluded pairs: {Pairs}", string.Join(", ", added));
            }

            return Ok(new { added, excludedPairs = c.Optimization.ExcludedPairs });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add excluded pairs");
            return StatusCode(500, new { error = "Failed to save excluded pairs" });
        }
    }

    // ─── Walk test ───────────────────────────────────────────────────────────

    public class WalkTestStartRequest
    {
        public string DeviceId { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public int? DurationSecs { get; set; }
    }

    [HttpGet("api/wizard/walktest/status")]
    public IActionResult WalkTestStatus()
    {
        return Ok(walkTest.Status());
    }

    [HttpPost("api/wizard/walktest/start")]
    public IActionResult WalkTestStart([FromBody] WalkTestStartRequest req)
    {
        var (ok, error) = walkTest.Start(req.DeviceId, req.X, req.Y, req.Z, req.DurationSecs);
        return ok ? Ok(new { started = true }) : BadRequest(new { error });
    }

    [HttpPost("api/wizard/walktest/stop")]
    public IActionResult WalkTestStop()
    {
        var (ok, error, point) = walkTest.Stop();
        return ok ? Ok(new { stopped = true, point }) : BadRequest(new { error });
    }

    [HttpPost("api/wizard/walktest/cancel")]
    public IActionResult WalkTestCancel()
    {
        var (ok, error) = walkTest.Cancel();
        return ok ? Ok(new { cancelled = true }) : BadRequest(new { error });
    }

    [HttpDelete("api/wizard/walktest/points/{id}")]
    public IActionResult WalkTestDeletePoint(string id)
    {
        return walkTest.DeletePoint(id) ? Ok(new { deleted = true }) : NotFound(new { error = $"Point '{id}' not found" });
    }

    [HttpGet("api/wizard/walktest/suggest")]
    public IActionResult WalkTestSuggest()
    {
        return Ok(new { suggestions = walkTest.SuggestPoints() });
    }

    // ─── Auto-tune (optimizer/hyperparameter selection) ─────────────────────

    [HttpGet("api/wizard/autotune/status")]
    public IActionResult AutoTuneStatus()
    {
        return Ok(autoTune.Status());
    }

    [HttpPost("api/wizard/autotune/start")]
    public IActionResult AutoTuneStart()
    {
        var (ok, error) = autoTune.Start();
        return ok ? Ok(new { started = true }) : BadRequest(new { error });
    }

    public class AutoTuneApplyRequest
    {
        public string CandidateKey { get; set; } = "";
    }

    [HttpPost("api/wizard/autotune/apply")]
    public async Task<IActionResult> AutoTuneApply([FromBody] AutoTuneApplyRequest req)
    {
        var (ok, error) = await autoTune.Apply(req.CandidateKey);
        return ok ? Ok(new { applied = true }) : BadRequest(new { error });
    }

    // ─── Locator tune (walk-test replay) ────────────────────────────────────

    [HttpGet("api/wizard/locatortune/status")]
    public IActionResult LocatorTuneStatus()
    {
        return Ok(locatorTune.Status());
    }

    [HttpPost("api/wizard/locatortune/run")]
    public IActionResult LocatorTuneRun()
    {
        // Synchronous - pure math over recorded points, finishes in well under a second.
        return Ok(locatorTune.Run());
    }

    [HttpPost("api/wizard/locatortune/apply")]
    public async Task<IActionResult> LocatorTuneApply([FromBody] AutoTuneApplyRequest req)
    {
        var (ok, error) = await locatorTune.Apply(req.CandidateKey);
        return ok ? Ok(new { applied = true }) : BadRequest(new { error });
    }

    // ─── Settings (optimization + locators config) ──────────────────────────

    public class WizardSettings
    {
        public int IntervalSecs { get; set; }
        public int KeepSnapshotMins { get; set; }
        public Dictionary<string, double> Limits { get; set; } = new();
        public Dictionary<string, double> Weights { get; set; } = new();
        public bool NadarayaWatsonEnabled { get; set; }
        public double NadarayaWatsonBandwidth { get; set; }
        public string NadarayaWatsonKernel { get; set; } = "gaussian";
        public bool NelderMeadEnabled { get; set; }
        public bool MleEnabled { get; set; }
        public bool MultiFloorEnabled { get; set; }
        public bool NearestNodeEnabled { get; set; }
        public double? NearestNodeMaxDistance { get; set; }
    }

    [HttpGet("api/wizard/settings")]
    public IActionResult GetSettings()
    {
        var c = configLoader.Config;
        if (c == null) return StatusCode(500, new { error = "Config not loaded" });
        return Ok(new WizardSettings
        {
            IntervalSecs = c.Optimization.IntervalSecs,
            KeepSnapshotMins = c.Optimization.KeepSnapshotMins,
            Limits = c.Optimization.Limits,
            Weights = c.Optimization.Weights,
            NadarayaWatsonEnabled = c.Locators.NadarayaWatson.Enabled,
            NadarayaWatsonBandwidth = c.Locators.NadarayaWatson.Bandwidth,
            NadarayaWatsonKernel = c.Locators.NadarayaWatson.Kernel,
            NelderMeadEnabled = c.Locators.NelderMead.Enabled,
            MleEnabled = c.Locators.Mle.Enabled,
            MultiFloorEnabled = c.Locators.MultiFloor.Enabled,
            NearestNodeEnabled = c.Locators.NearestNode.Enabled,
            NearestNodeMaxDistance = c.Locators.NearestNode.MaxDistance
        });
    }

    [HttpPost("api/wizard/settings")]
    public async Task<IActionResult> SaveSettings([FromBody] WizardSettings s)
    {
        try
        {
            var c = configLoader.Config;
            if (c == null) return StatusCode(500, new { error = "Config not loaded" });

            c.Optimization.IntervalSecs = Math.Clamp(s.IntervalSecs, 15, 86400);
            c.Optimization.KeepSnapshotMins = Math.Clamp(s.KeepSnapshotMins, 1, 120);
            foreach (var (k, v) in s.Limits) c.Optimization.Limits[k] = v;
            foreach (var (k, v) in s.Weights) c.Optimization.Weights[k] = v;

            c.Locators.NadarayaWatson.Enabled = s.NadarayaWatsonEnabled;
            c.Locators.NadarayaWatson.Bandwidth = s.NadarayaWatsonBandwidth;
            c.Locators.NadarayaWatson.Kernel = s.NadarayaWatsonKernel;
            c.Locators.NelderMead.Enabled = s.NelderMeadEnabled;
            c.Locators.Mle.Enabled = s.MleEnabled;
            c.Locators.MultiFloor.Enabled = s.MultiFloorEnabled;
            c.Locators.NearestNode.Enabled = s.NearestNodeEnabled;
            c.Locators.NearestNode.MaxDistance = s.NearestNodeMaxDistance;

            await configLoader.SaveSectionAsync("optimization", c.Optimization);
            await configLoader.SaveSectionAsync("locators", c.Locators);
            Log.Information("Wizard settings saved (optimization + locators sections)");
            return Ok(new { saved = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save wizard settings");
            return StatusCode(500, new { error = "Failed to save settings" });
        }
    }
}
