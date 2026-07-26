using System.Globalization;
using ESPresense.Utils;

namespace ESPresense.Companion.Tests.Utils;

/// <summary>
/// Node payloads are machine-formatted and must parse the same way everywhere. Found by a
/// NodeMoveTracker test that expected an absorption of 4.2 and got 42 on a German-locale machine.
/// </summary>
public class ParsingUtilsTests
{
    [TestCase("de-DE")]
    [TestCase("fr-FR")]
    [TestCase("en-US")]
    [TestCase("")]
    public void ParsesTheSameUnderAnyCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.That(ParsingUtils.ParseDoubleOrDefault("4.2"), Is.EqualTo(4.2).Within(1e-9),
                "a dot is a decimal point in this protocol, never a group separator");
            Assert.That(ParsingUtils.ParseDoubleOrDefault("-77.5"), Is.EqualTo(-77.5).Within(1e-9));
            Assert.That(ParsingUtils.ParseIntOrDefault("-59"), Is.EqualTo(-59));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public void ReturnsNullRatherThanZeroOnRubbish()
    {
        // The difference matters: absorption 0 is a value the optimizer would act on, absorption
        // "unknown" is one it skips.
        Assert.That(ParsingUtils.ParseDoubleOrDefault("n/a"), Is.Null);
        Assert.That(ParsingUtils.ParseDoubleOrDefault(""), Is.Null);
        Assert.That(ParsingUtils.ParseDoubleOrDefault(null), Is.Null);
        Assert.That(ParsingUtils.ParseIntOrDefault("4.2"), Is.Null);
    }
}
