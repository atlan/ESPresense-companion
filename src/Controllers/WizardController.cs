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
    ConfigLoader configLoader) : ControllerBase
{
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
            minSamples = PairErrorTracker.MinSamples,
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
}
