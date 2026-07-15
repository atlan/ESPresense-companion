using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class Floor
{
    [JsonIgnore]
    public Config? Config { get; private set; }

    public string? Id { get;private  set; }
    public string? Name { get; private set; }
    public Point3D[]? Bounds { get; private set; }

    public ConcurrentDictionary<string, Room> Rooms { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Update(Config c, ConfigFloor cf)
    {
        Point3D Point3DConvert(double[] b)
        {
            var x = b.Length > 0 ? b[0] : 0;
            var y = b.Length > 1 ? b[1] : 0;
            var z = b.Length > 2 ? b[2] : 0;
            return new Point3D(x, y, z);
        }

        Config = c;
        Name = cf.Name;
        Id = cf.GetId();
        var bounds = cf.Bounds?.Select(Point3DConvert).ToArray();
        // Normalize to (min, max) per axis. A misconfigured floor (e.g. the second point's Z
        // entered as a ceiling height instead of an absolute Z coordinate) can produce min > max
        // on some axis, which throws downstream (Math.Clamp requires min <= max) and crashes any
        // locator that clamps into floor bounds (e.g. MLEMultilateralizer).
        if (bounds is { Length: >= 2 })
        {
            var (a, b) = (bounds[0], bounds[1]);
            bounds[0] = new Point3D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
            bounds[1] = new Point3D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        }
        Bounds = bounds;

        foreach (var room in cf.Rooms ?? Enumerable.Empty<ConfigRoom>()) Rooms.GetOrAdd(room.GetId(), a => new Room()).Update(c, this, room);
    }

    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }

    public bool Contained(double? z)
    {
        if (z == null) return false;
        if (Bounds == null || Bounds.Length < 2) return false;
        return z >= Bounds[0].Z && z <= Bounds[1].Z;
    }
}
