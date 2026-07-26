using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

/// <summary>
/// Decides between floors using the readings every floor scenario currently discards.
///
/// Each locator filters to nodes on its own floor, so a device standing in the basement is fitted
/// three times - once per storey - from three disjoint sets of nodes, and the storey with the better
/// fit wins. On this installation that throws away 44 % of all readings, and at exactly the spots
/// where the decision goes wrong the discarded readings are the MAJORITY: walk point wt9 in the
/// boiler room is heard by two basement nodes and six on the floor above, which is why it currently
/// cannot be placed at all.
///
/// Feeding those readings into every scenario as distances is the obvious move and would be the
/// wrong one: all three fits would then draw on nearly the same data and stop being distinguishable,
/// damaging the very thing that makes the floor decision possible.
///
/// Used as a CONTRAST they do the opposite. A ceiling attenuates, so a node one storey away reports
/// a longer distance than the geometry allows. If a candidate floor is the right one, that surplus
/// appears in the foreign nodes and not in its own. If the device is really one floor down, the
/// "foreign" nodes are the ones standing next to it - they read short, and the contrast inverts.
///
/// Measured over 32 walk points before this was built, because the same reasoning had already failed
/// twice today when checked against data: median log10(reported / geometric distance) runs about
/// 0.2 higher for nodes one ceiling away, positive at 29 of 32 points, 0.206 where floor detection
/// works against 0.103 where it struggles, and -0.011 at wt32 - the worst point on the installation.
/// Then measured again after: floor detection 87.9 % to 96.5 % at weight 20, monotone in between.
///
/// Deliberately built on the ratio of reported to geometric distance rather than on signal levels.
/// The ratio needs no per-node absorption and therefore cannot inherit the calibration's errors -
/// which matters, because the readings it judges are the cross-floor ones the calibration explicitly
/// excludes and knows nothing about.
/// </summary>
public static class FloorContrast
{
    /// <summary>
    /// Contrast a correct floor assignment produces on this installation: about 0.2 in log10 terms,
    /// roughly 6 dB per ceiling. Used to normalize, so the weight below is in confidence points.
    /// </summary>
    public const double Expected = 0.2;

    /// <summary>Closer than this the ratio is dominated by the node's own position error.</summary>
    private const double MinDistanceM = 0.5;

    /// <summary>Fewer than this on either side and one reflection decides the storey.</summary>
    private const int MinPerSide = 2;

    /// <summary>
    /// Confidence adjustment for a candidate floor, clamped to +/- <paramref name="weight"/> points.
    /// Returns 0 when there is nothing to compare - no foreign nodes heard, or too few on either side.
    /// </summary>
    /// <param name="estimate">Position this candidate floor fitted, the reference for the geometry.</param>
    /// <param name="readings">Every audible node with its reported distance, own floor and others alike.</param>
    /// <param name="onCandidateFloor">Whether a reading's node belongs to the floor being judged.</param>
    public static double Adjustment<T>(
        Point3D estimate,
        IEnumerable<T> readings,
        Func<T, Point3D> location,
        Func<T, double> distance,
        Func<T, bool> onCandidateFloor,
        double weight)
    {
        if (weight <= 0) return 0;

        var own = new List<double>();
        var other = new List<double>();

        foreach (var reading in readings)
        {
            var reported = distance(reading);
            if (reported <= 0) continue;
            var geometric = estimate.DistanceTo(location(reading));
            if (geometric < MinDistanceM) continue;
            (onCandidateFloor(reading) ? own : other).Add(Math.Log10(reported / geometric));
        }

        if (own.Count < MinPerSide || other.Count < MinPerSide) return 0;

        var contrast = Median(other) - Median(own);
        return Math.Clamp(contrast / Expected, -1.0, 1.0) * weight;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
