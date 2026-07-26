using ESPresense.Locators;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// The cross-floor contrast, which decides between storeys using the readings each floor scenario
/// otherwise discards. The cases below are the ones the live data produced, not invented ones:
/// a correct floor, a device actually one storey down, and the two ways there is nothing to say.
/// </summary>
public class FloorContrastTests
{
    private record Reading(Point3D Location, double Distance, bool OwnFloor);

    private static double Adjust(IEnumerable<Reading> readings, Point3D estimate, double weight = 20) =>
        FloorContrast.Adjustment(estimate, readings, r => r.Location, r => r.Distance, r => r.OwnFloor, weight);

    /// <summary>
    /// Nodes at a known distance from the estimate, reporting that distance inflated by the given
    /// factor - which is exactly what a ceiling does to a reading.
    /// </summary>
    private static List<Reading> Nodes(int count, double geometric, double inflation, bool ownFloor, double z) =>
        Enumerable.Range(0, count)
            .Select(i =>
            {
                var angle = i * 2 * Math.PI / count;
                var loc = new Point3D(Math.Cos(angle) * geometric, Math.Sin(angle) * geometric, z);
                return new Reading(loc, new Point3D(0, 0, z).DistanceTo(loc) * inflation, ownFloor);
            })
            .ToList();

    [Test]
    public void RewardsTheFloorWhoseForeignNodesReadLong()
    {
        // The device is on the candidate floor: its own nodes report roughly the true distance,
        // the storey above reports further than geometry allows because a ceiling is in the way.
        var readings = Nodes(3, 4, 1.0, ownFloor: true, z: 0)
            .Concat(Nodes(3, 4, 1.6, ownFloor: false, z: 0))
            .ToList();

        Assert.That(Adjust(readings, Point3D.Origin), Is.GreaterThan(15),
            "a ceiling's worth of surplus in the foreign readings is what confirms the storey");
    }

    [Test]
    public void PunishesTheFloorWhoseForeignNodesReadShort()
    {
        // The inverted case, and the one that matters: the device is really one storey away, so the
        // nodes this candidate calls "foreign" are the ones standing next to it and read short.
        // Without this the two floors differ by a few confidence points out of 100 and the decision
        // is a coin flip - measured at three walk points on the real installation.
        var readings = Nodes(3, 4, 1.6, ownFloor: true, z: 0)
            .Concat(Nodes(3, 4, 1.0, ownFloor: false, z: 0))
            .ToList();

        Assert.That(Adjust(readings, Point3D.Origin), Is.LessThan(-15));
    }

    [Test]
    public void SaysNothingWithoutForeignNodes()
    {
        // A single-storey home, or a device only its own floor can hear. Returning 0 rather than a
        // guess keeps the confidence exactly as it was before this term existed.
        Assert.That(Adjust(Nodes(4, 4, 1.2, ownFloor: true, z: 0), Point3D.Origin), Is.Zero);
    }

    [Test]
    public void SaysNothingOnASingleForeignReading()
    {
        var readings = Nodes(3, 4, 1.0, ownFloor: true, z: 0)
            .Concat(Nodes(1, 4, 3.0, ownFloor: false, z: 0))
            .ToList();

        Assert.That(Adjust(readings, Point3D.Origin), Is.Zero,
            "one reflection off a stairwell wall must not be allowed to move a device between storeys");
    }

    [Test]
    public void StaysWithinTheGivenWeight()
    {
        // An absurd contrast must not overrun the fit it is adjusting - the term is a tie-breaker
        // between floors, not a second opinion on where the device is.
        var readings = Nodes(3, 4, 1.0, ownFloor: true, z: 0)
            .Concat(Nodes(3, 4, 12.0, ownFloor: false, z: 0))
            .ToList();

        Assert.That(Adjust(readings, Point3D.Origin, weight: 20), Is.EqualTo(20).Within(0.001));
    }

    [Test]
    public void DisabledAtWeightZero()
    {
        var readings = Nodes(3, 4, 1.0, ownFloor: true, z: 0)
            .Concat(Nodes(3, 4, 1.6, ownFloor: false, z: 0))
            .ToList();

        Assert.That(Adjust(readings, Point3D.Origin, weight: 0), Is.Zero);
    }

    [Test]
    public void IgnoresNodesTooCloseForTheRatioToMeanAnything()
    {
        // At 10 cm the ratio is dominated by how precisely the node was placed on the map, not by
        // what the radio did. Three foreign nodes, two of them unusably close, leaves one - and one
        // is not enough, so the answer is silence rather than noise.
        var readings = Nodes(3, 4, 1.0, ownFloor: true, z: 0)
            .Concat(new[]
            {
                new Reading(new Point3D(0.1, 0, 0), 5.0, false),
                new Reading(new Point3D(0, 0.1, 0), 5.0, false),
                new Reading(new Point3D(4, 0, 0), 6.4, false)
            })
            .ToList();

        Assert.That(Adjust(readings, Point3D.Origin), Is.Zero);
    }
}
