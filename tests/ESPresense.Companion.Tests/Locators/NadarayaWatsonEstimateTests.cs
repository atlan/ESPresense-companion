using ESPresense.Locators;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// Tests for the extracted static NW weighted-centroid math - the wizard's locator replay depends
/// on this being EXACTLY the live locator's computation, so its invariants are pinned here.
/// </summary>
public class NadarayaWatsonEstimateTests
{
    [Test]
    public void Estimate_EquidistantNodes_ReturnsCentroid()
    {
        var heard = new List<(Point3D loc, double dist)>
        {
            (new Point3D(0, 0, 0), 2),
            (new Point3D(4, 0, 0), 2),
            (new Point3D(2, 4, 0), 2)
        };

        var (est, _) = NadarayaWatsonMultilateralizer.Estimate(heard, 1.0, "gaussian");

        Assert.That(est.X, Is.EqualTo(2).Within(0.001));
        Assert.That(est.Y, Is.EqualTo(4.0 / 3).Within(0.001));
    }

    [Test]
    public void Estimate_CloserNodePullsEstimate()
    {
        var heard = new List<(Point3D loc, double dist)>
        {
            (new Point3D(0, 0, 0), 1),
            (new Point3D(4, 0, 0), 3)
        };

        var (estGauss, _) = NadarayaWatsonMultilateralizer.Estimate(heard, 1.0, "gaussian");
        var (estInv, _) = NadarayaWatsonMultilateralizer.Estimate(heard, 0, "inverse_square");

        Assert.That(estGauss.X, Is.LessThan(2), "gaussian: estimate must sit closer to the nearer node");
        Assert.That(estInv.X, Is.LessThan(2), "inverse: estimate must sit closer to the nearer node");
    }

    [Test]
    public void Estimate_LargerBandwidthWeightsFartherNodesMore()
    {
        var heard = new List<(Point3D loc, double dist)>
        {
            (new Point3D(0, 0, 0), 1),
            (new Point3D(4, 0, 0), 3)
        };

        var (narrow, _) = NadarayaWatsonMultilateralizer.Estimate(heard, 0.5, "gaussian");
        var (wide, _) = NadarayaWatsonMultilateralizer.Estimate(heard, 3.0, "gaussian");

        Assert.That(wide.X, Is.GreaterThan(narrow.X),
            "a wider bandwidth flattens the kernel, pulling the estimate towards the farther node");
    }

    [Test]
    public void Estimate_ZeroBandwidthFallsBackToInverse()
    {
        var heard = new List<(Point3D loc, double dist)>
        {
            (new Point3D(0, 0, 0), 10),
            (new Point3D(4, 0, 0), 12)
        };

        // With bandwidth ~0 all gaussian weights underflow to ~0 - the guard must fall back to
        // inverse-distance weighting instead of dividing by zero.
        var (est, err) = NadarayaWatsonMultilateralizer.Estimate(heard, 1e-9, "gaussian");

        Assert.That(double.IsFinite(est.X), Is.True);
        Assert.That(double.IsFinite(err), Is.True);
        Assert.That(est.X, Is.InRange(0, 4));
    }
}
