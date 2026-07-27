using ESPresense.Models;

namespace ESPresense.Optimizers;

public interface IOptimizer
{
    public string Name { get; }
    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings);

    /// <summary>
    /// Wird dieser Optimierer gegen die Bodenwahrheit (Walk-Punkte) bewertet statt gegen die
    /// Uebereinstimmung der Knoten untereinander?
    ///
    /// Noetig, weil beide Ziele einander widersprechen koennen - gemessen am 27.07.2026. Ein
    /// Kandidat, der die Ortung verbessert, verschlechtert dabei oft das Knoten-Paar-Komposit und
    /// wuerde von dessen Gate abgelehnt. Fuer solche Optimierer entscheidet allein das
    /// Walk-Punkt-Gate.
    /// </summary>
    public bool ScoredByWalkPoints => false;
}