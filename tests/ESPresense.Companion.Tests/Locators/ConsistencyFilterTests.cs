using ESPresense.Locators;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// The filter runs in the live locator, and until these tests existed only its early exit had ever
/// been executed - the greedy selection that actually decides which readings survive had 4 % branch
/// coverage. The decision to enable it rested entirely on a benchmark over one house's walk points,
/// which measures whether it helps there, not whether it does what it claims anywhere.
/// </summary>
public class ConsistencyFilterTests
{
    private record R(Point3D Loc, double Dist);

    private static IReadOnlyList<R> Filter(
        IReadOnlyList<R> readings,
        double toleranceM = ConsistencyFilter.DefaultToleranceM,
        double fraction = ConsistencyFilter.DefaultToleranceFraction) =>
        ConsistencyFilter.LargestConsistent(readings, r => r.Loc, r => r.Dist, toleranceM, fraction);

    private static R At(double x, double y, double reported) => new(new Point3D(x, y, 0), reported);

    /// <summary>
    /// ★ The case the filter was written for, and the shipped tolerance does NOT catch it.
    ///
    /// Live on 2026-07-26 the kitchen node reported 0.86 m for a device 7.03 m away, while toilette
    /// reported 1.30 m from a node 7 m from kitchen. The class comment presents this as the
    /// motivating example. Run the arithmetic with the values actually in force: slack is
    /// 1.5 + 0.5 x 7 = 5.0 m, and 0.86 + 1.30 + 5.0 = 7.16, which clears 7.0. Compatible.
    ///
    /// The tolerance FRACTION is what does it - at any real node separation it dwarfs the constant,
    /// so the triangle inequality stops biting exactly where the nodes are far apart and the
    /// contradiction is most blatant. Recorded as a test rather than silently retuned, because the
    /// swept optimum (1.5 m, room hit rate 62 %) is a measurement and this is a second one; they
    /// disagree, and the disagreement is the finding.
    /// </summary>
    [Test]
    public void ShippedToleranceDoesNotCatchItsOwnMotivatingCase()
    {
        var readings = new[]
        {
            At(0, 0, 0.86),    // kitchen, the liar
            At(7, 0, 1.30),    // toilette
            At(7, 6, 6.20),
            At(0, 6, 6.00),
            At(3, 3, 3.10)
        };

        Assert.That(Filter(readings), Is.SameAs(readings),
            "documented as caught, measurably not caught at tolerance 1.5 m + 50 %");

        // With the separation-proportional part reduced, the same readings are rejected.
        var kept = Filter(readings, toleranceM: 1.5, fraction: 0.2);
        Assert.That(kept, Has.Count.LessThan(readings.Length));
        Assert.That(kept.Any(r => Math.Abs(r.Dist - 0.86) < 0.01), Is.False,
            "the reading contradicting the most others is the one that goes");
    }

    [Test]
    public void LeavesMutuallyConsistentReadingsAlone()
    {
        // Distances that a real point could produce, short by the usual systematic amount. Returning
        // the input unchanged matters: this is the common case, and rebuilding the list every tick
        // for nothing would be pure cost.
        var truth = new Point3D(3, 3, 0);
        var readings = new[]
        {
            new R(new Point3D(0, 0, 0), truth.DistanceTo(new Point3D(0, 0, 0)) * 0.6),
            new R(new Point3D(7, 0, 0), truth.DistanceTo(new Point3D(7, 0, 0)) * 0.6),
            new R(new Point3D(7, 6, 0), truth.DistanceTo(new Point3D(7, 6, 0)) * 0.6),
            new R(new Point3D(0, 6, 0), truth.DistanceTo(new Point3D(0, 6, 0)) * 0.6)
        };

        Assert.That(Filter(readings), Is.SameAs(readings),
            "nothing contradicts anything, so the input itself comes back");
    }

    [Test]
    public void KeepsEverythingWhenThereIsNothingToCrossCheckAgainst()
    {
        // Three readings cannot outvote each other - whichever is wrong, the other two have no
        // majority. Filtering here would be guessing.
        var readings = new[] { At(0, 0, 0.5), At(7, 0, 0.5), At(7, 6, 9.0) };

        Assert.That(Filter(readings), Is.SameAs(readings));
    }

    [Test]
    public void NeverStripsBelowWhatALocatorNeeds()
    {
        // Deliberately absurd: every reading contradicts every other. A filter that strips down to
        // one reading turns a bad fix into no fix, which is worse - the device disappears instead of
        // being misplaced.
        var readings = new[]
        {
            At(0, 0, 0.1), At(20, 0, 0.1), At(20, 20, 0.1), At(0, 20, 0.1), At(10, 10, 0.1)
        };

        Assert.That(Filter(readings), Has.Count.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void ThePartyOfFourOutvotesThePairThatAgreesWithEachOther()
    {
        // Two readings can be consistent with one another and wrong about everything else. The rule
        // is largest consistent group, not "drop whatever disagrees with the first one".
        var truth = new Point3D(2, 2, 0);
        var honest = new[] { new Point3D(0, 0, 0), new Point3D(5, 0, 0), new Point3D(5, 5, 0), new Point3D(0, 5, 0) }
            .Select(p => new R(p, truth.DistanceTo(p)))
            .ToList();
        // Two nodes far away, both claiming to be right next to the device and next to each other.
        var conspirators = new[] { new R(new Point3D(30, 30, 0), 0.4), new R(new Point3D(30.5, 30, 0), 0.4) };

        var kept = Filter(honest.Concat(conspirators).ToList());

        Assert.That(kept, Has.Count.EqualTo(4));
        Assert.That(kept.All(r => r.Loc.X < 10), Is.True,
            "four readings that agree beat two that only agree with each other");
    }

    [Test]
    public void ToleranceDecidesWhatCountsAsImpossible()
    {
        // A pair that is merely inaccurate rather than impossible. Measured distances in a real
        // installation run 20-50 % short, so the slack has to be generous or the filter starts
        // discarding the very readings it exists to protect.
        var readings = new[] { At(0, 0, 1.2), At(4, 0, 1.2), At(4, 4, 4.5), At(0, 4, 4.5) };

        Assert.That(Filter(readings), Is.SameAs(readings),
            "at the shipped tolerance these are short, not contradictory");

        // Note which knob had to move. Dropping the constant to almost nothing changes nothing at
        // all, because 50 % of a 4 m separation is still 2 m of slack - the fraction is the knob
        // that decides, and it is the one the tolerance sweep never varied.
        Assert.That(Filter(readings, toleranceM: 0.01), Is.SameAs(readings));
        Assert.That(Filter(readings, toleranceM: 0.01, fraction: 0.0), Has.Count.LessThan(readings.Length),
            "with the separation-proportional slack gone the same readings no longer fit together");
    }
}
