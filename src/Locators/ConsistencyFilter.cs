using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

/// <summary>
/// Drops readings that cannot all be true at once, before any position is estimated.
///
/// The problem it solves, observed live on 2026-07-26: the kitchen node reported 0.86 m for a device
/// that was 7.03 m away. <see cref="NadarayaWatsonMultilateralizer"/> is a kernel-weighted average of
/// NODE POSITIONS, weighted by the reported distance, so a node claiming to be close pulls the
/// estimate onto itself - that single reading dragged the solution three metres west. Neither of the
/// obvious defences catches it: rank weighting promotes the false reading to first place precisely
/// because it looks closest, and variance weighting trusts it because it was steady (var 0.05). It
/// was not noisy. It was consistently wrong.
///
/// What does catch it is geometry. Two nodes at known positions constrain each other through the
/// triangle inequality: a device within d_i of node i and d_j of node j requires
/// <c>|d_i - d_j| &lt;= dist(i,j) &lt;= d_i + d_j</c>. Saying so needs no estimate, no iteration and no
/// starting guess - which matters, because iterative down-weighting started from a solution the
/// outlier already captured would reject the honest readings instead.
///
/// ★ Correction to what this comment used to claim. It presented kitchen's 0.86 m plus toilette's
/// 1.30 m against a 7 m separation as the case being caught. At the shipped tolerance it is not:
/// slack is 1.5 + 0.5 x 7 = 5.0 m, and 2.16 + 5.0 clears 7.0 comfortably. The example motivated the
/// filter; it is not an example of what the filter does.
///
/// Both settings were then swept against the walk points, and the shipped pair is right anyway:
/// at fraction 0.5 the median error is 1.86 m and the room hit rate 62 %, while tightening towards
/// the value that WOULD catch the kitchen case collapses to 3.09 m and 20 % - it discards honest
/// readings by the thousand to catch one. So the filter earns its place on a different and far more
/// common class of contradiction than the one it was named after.
///
/// The largest mutually consistent group wins. With one bad reading among several good ones the good
/// ones agree with each other and it does not, so it is the one left out.
/// </summary>
public static class ConsistencyFilter
{
    /// <summary>
    /// Slack on the triangle inequality, as a constant plus a share of the node separation.
    /// Generous on purpose: the aim is to catch readings that are impossible, not merely inaccurate,
    /// and the measured distances in a real installation are routinely 20-50 % short.
    /// </summary>
    public const double DefaultToleranceM = 1.5;
    public const double DefaultToleranceFraction = 0.5;

    /// <summary>
    /// Gewicht der Messunsicherheit im Spielraum, in Standardabweichungen. 0 = aus, dann verhaelt
    /// sich der Filter exakt wie bisher.
    ///
    /// Die Idee dahinter: die feste Toleranz behandelt alle Knoten gleich, obwohl ihre
    /// Zuverlaessigkeit um zwei Groessenordnungen auseinanderliegt - am 27.07.2026 gemessen 0,2 bis
    /// 42,8 Pegelvarianz in derselben Anlage. Ein Paar aus zwei ruhigen Knoten kann man eng fassen,
    /// eines mit einem unruhigen Knoten muss man weit fassen, und ein einziger konstanter Wert ist
    /// fuer beide falsch. Die Varianz je Messung liegt ohnehin vor (DistVar), sie wurde nur nie
    /// benutzt.
    ///
    /// Bewusst mit Default 0 ausgeliefert: ob es den festen Wert schlaegt, entscheidet der
    /// Walk-Punkt-Prüfstand, nicht diese Begruendung.
    /// </summary>
    public const double DefaultVarianceWeight = 0.0;

    /// <summary>Below this many readings there is nothing to cross-check against - keep them all.</summary>
    private const int MinForFiltering = 4;

    /// <summary>
    /// Returns the largest subset whose reported distances are mutually compatible. Falls back to
    /// the input unchanged when there is too little to compare or nothing to reject.
    /// </summary>
    public static IReadOnlyList<T> LargestConsistent<T>(
        IReadOnlyList<T> readings,
        Func<T, Point3D> location,
        Func<T, double> distance,
        double toleranceM = DefaultToleranceM,
        double toleranceFraction = DefaultToleranceFraction,
        Func<T, double?>? variance = null,
        double varianceWeight = DefaultVarianceWeight)
    {
        var n = readings.Count;
        if (n < MinForFiltering) return readings;

        var compatible = new bool[n, n];
        var degree = new int[n];
        for (var i = 0; i < n; i++)
        {
            compatible[i, i] = true;
            for (var j = i + 1; j < n; j++)
            {
                var separation = location(readings[i]).DistanceTo(location(readings[j]));
                var di = distance(readings[i]);
                var dj = distance(readings[j]);
                var slack = toleranceM + toleranceFraction * separation;

                // Messunsicherheit beider Beteiligten dazu, in Metern: die Dreiecksungleichung muss
                // nur innerhalb dessen halten, was die Messung ueberhaupt aufloest. Sigma statt
                // Varianz, weil der Spielraum eine Laenge ist und keine Flaeche.
                if (varianceWeight > 0 && variance != null)
                {
                    var vi = variance(readings[i]);
                    var vj = variance(readings[j]);
                    if (vi is > 0) slack += varianceWeight * Math.Sqrt(vi.Value);
                    if (vj is > 0) slack += varianceWeight * Math.Sqrt(vj.Value);
                }

                // Both spheres must be able to intersect: not too far apart, not one swallowing the other.
                var ok = di + dj + slack >= separation && Math.Abs(di - dj) - slack <= separation;
                compatible[i, j] = compatible[j, i] = ok;
                if (ok) { degree[i]++; degree[j]++; }
            }
        }

        // Nobody contradicts anybody - nothing to do, and this is the common case.
        if (degree.All(d => d == n - 1)) return readings;

        // Greedy rather than a true maximum clique: drop the reading that contradicts the most
        // others, recount, repeat. Exact maximum clique is NP-hard and pointless here - with one or
        // two bad readings among a handful, the worst offender is unambiguous.
        var alive = Enumerable.Range(0, n).ToList();
        while (true)
        {
            var worst = -1;
            var worstConflicts = 0;
            foreach (var i in alive)
            {
                var conflicts = alive.Count(j => j != i && !compatible[i, j]);
                if (conflicts > worstConflicts) { worstConflicts = conflicts; worst = i; }
            }
            if (worst < 0) break;                       // everyone left agrees
            if (alive.Count <= MinForFiltering - 1) break;   // never strip it down to nothing
            alive.Remove(worst);
        }

        return alive.Count == n ? readings : alive.Select(i => readings[i]).ToList();
    }
}
