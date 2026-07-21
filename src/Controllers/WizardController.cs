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
    WalkTestService walkTest) : ControllerBase
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
}
