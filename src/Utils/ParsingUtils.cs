using System.Globalization;

namespace ESPresense.Utils;

/// <summary>
/// Parsing for values that arrive over MQTT from the nodes.
///
/// Invariant culture, explicitly. These are machine-formatted payloads with a dot for the decimal
/// point, and the framework default follows the CURRENT culture - so on a German or French host
/// "4.2" parses as 42, the dot being read as a group separator. Absorption off by a factor of ten,
/// silently, with no parse failure to notice.
///
/// The published container sets DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true, so this has never bitten
/// in production. It bit a unit test instead, which is the only reason it was found - and it would
/// have come back the moment anyone ran the Companion outside that container.
/// </summary>
public static class ParsingUtils
{
    public static double? ParseDoubleOrDefault(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static int? ParseIntOrDefault(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
