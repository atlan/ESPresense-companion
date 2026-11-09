using ESPresense.Locators;
using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>What one fusion pass chose for a device.</summary>
/// <param name="Best">The scenario to report, or null when no scenario is current.</param>
/// <param name="Moved">How many scenarios' locators reported a new position this pass.</param>
public sealed record FusionResult(Scenario? Best, int Moved);

/// <summary>
/// Combines a device's scenarios into one answer. No MQTT, history or events: everything it
/// touches is on the <see cref="Device"/> and its <see cref="Scenario"/>s.
///   1. Runs every scenario's locator.
///   2. Feeds the scenario with the highest raw Confidence into the device-level Kalman filter.
///   3. Weights each scenario's confidence by motion consistency (Gaussian penalty against the
///      Kalman predicted location) and smooths per-scenario probabilities over time.
///   4. Picks the current scenario with the highest smoothed probability; ties go to the higher
///      confidence, then to the earlier scenario.
/// </summary>
public static class ScenarioFusion
{
    public static FusionResult Update(Device device, ConfigFiltering filtering, DateTime now, double floorContrastWeight = 0)
    {
        device.LastCalculated = now;
        var moved = device.Scenarios.AsParallel().Count(s => s.Locate());

        // Each scenario is fitted from the nodes of one floor, so the readings from every other floor are
        // discarded by all of them alike. Used as a contrast rather than as distances they say which storey
        // the device is on: a ceiling attenuates, so nodes one floor away report longer than the geometry
        // allows, and on a wrong candidate floor that surplus shows up on the wrong side and the sign flips.
        //
        // Applied here and not inside a locator: with several locators enabled there is one scenario per
        // locator AND per floor, and a contrast on only one of them lost to another locator's wrong floor
        // (live measurement 2026-07-27). Floor selection happens here, so the floor term belongs here.
        if (floorContrastWeight > 0)
        {
            var audible = device.Nodes.Values.Where(n => n.Current && n.Node is { HasLocation: true }).ToArray();
            if (audible.Length > 0)
                foreach (var scenario in device.Scenarios)
                {
                    if (scenario.Floor is not { } floor || scenario.Confidence is not { } confidence) continue;
                    var adjusted = confidence + FloorContrast.Adjustment(
                        scenario.Location, audible,
                        n => n.Node!.Location, n => n.Distance,
                        n => n.Node!.Floors?.Contains(floor) ?? false,
                        floorContrastWeight);
                    scenario.Confidence = (int)Math.Round(Math.Clamp(adjusted, 0, 100));
                }
        }

        var kalmanSource = device.Scenarios
            .Where(s => s.Current)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

        if (kalmanSource != null)
            device.UpdateLocation(kalmanSource.Location);

        // Predicted location from the updated Kalman filter (x̂ₖ|ₖ⁻¹)
        var (predictedLocation, _) = device.KalmanFilter.GetPrediction();

        foreach (var scenario in device.Scenarios)
        {
            if (!scenario.Current)
            {
                scenario.WeightedConfidence = 0;
                continue;
            }

            double delta = predictedLocation.DistanceTo(scenario.Location);
            double mcw = Math.Exp(-(delta * delta) / (2 * filtering.MotionSigma * filtering.MotionSigma));
            scenario.WeightedConfidence = (scenario.Confidence ?? 0) * mcw;
        }

        double totalWeightedConfidence = device.Scenarios.Sum(s => s.WeightedConfidence);
        if (totalWeightedConfidence > 0)
        {
            var priorWeight = filtering.SmoothingWeight;
            var newDataWeight = 1.0 - priorWeight;

            foreach (var scenario in device.Scenarios)
            {
                double newProb = scenario.WeightedConfidence / totalWeightedConfidence;
                scenario.Probability = priorWeight * scenario.Probability + newDataWeight * newProb;
            }

            double sumProb = device.Scenarios.Sum(s => s.Probability);
            if (sumProb > 0)
            {
                foreach (var scenario in device.Scenarios)
                    scenario.Probability /= sumProb;
            }
        }

        var best = device.Scenarios
            .Where(s => s.Current)
            .OrderByDescending(s => s.Probability)
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => device.Scenarios.IndexOf(s))
            .FirstOrDefault();

        device.BestScenario = best;
        return new FusionResult(best, moved);
    }
}
