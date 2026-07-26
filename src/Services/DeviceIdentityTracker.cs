using System.Collections.Concurrent;
using ESPresense.Events;

namespace ESPresense.Services;

/// <summary>
/// Detects one physical device arriving under several device ids.
///
/// Why this matters: a node derives the device id from whichever identifier it resolved first - MAC,
/// iBeacon UUID or IRK. Device aliases are keyed on that derived id
/// (<c>espresense/settings/&lt;id&gt;/config</c>), so a node that resolved a different identifier
/// looks the alias up under a key that does not exist and keeps publishing the raw id. One beacon
/// then arrives as two devices, each seen by a subset of the nodes, and multilateration silently
/// runs on a fraction of the measurements. Observed 2026-07-26: eight nodes reported
/// <c>ibeacon-osirisx</c>, two reported <c>iBeacon:fda50693-...</c>, same hardware - a fifth of the
/// data never reached the solution and nothing in the UI hinted at it.
///
/// ★ The hardware address is NOT a general identity anchor: phones and watches use resolvable
/// private addresses that rotate every few minutes (that is what the <c>irk:</c> device configs are
/// for). This tracker therefore watches BOTH directions and only trusts an address it has observed
/// to be stable - see <see cref="RotatingIdThreshold"/>. Devices with rotating addresses are
/// reported separately rather than silently ignored, because the correlation-based detector that
/// would cover them (same device = heard by the same nodes at the same time with correlated
/// levels) is a later stage.
/// </summary>
public class DeviceIdentityTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IdSighting>> _byMac = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reverse direction: how many distinct addresses each device id has appeared under.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _macsById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sightings older than this are ignored - a device renamed last week is not a live split.</summary>
    private static readonly TimeSpan SightingTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A device id seen under more than this many addresses within the TTL is treated as rotating
    /// (resolvable private address). Two is deliberate: a static-address device stays at exactly
    /// one, and a single rotation event during the window should not disqualify it.
    /// </summary>
    private const int RotatingIdThreshold = 2;

    public DeviceIdentityTracker(IMqttCoordinator mqtt)
    {
        mqtt.DeviceMessageReceivedAsync += OnDeviceMessage;
    }

    private Task OnDeviceMessage(DeviceMessageEventArgs e)
    {
        var mac = e.Payload?.Mac;
        if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(e.DeviceId)) return Task.CompletedTask;

        var ids = _byMac.GetOrAdd(mac, _ => new ConcurrentDictionary<string, IdSighting>(StringComparer.OrdinalIgnoreCase));
        ids.GetOrAdd(e.DeviceId, id => new IdSighting(id)).Touch(e.NodeId);

        _macsById.GetOrAdd(e.DeviceId, _ => new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase))[mac] = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    /// <summary>True if this id has been observed under several addresses recently, i.e. the address rotates.</summary>
    public bool HasRotatingAddress(string deviceId)
    {
        if (!_macsById.TryGetValue(deviceId, out var macs)) return false;
        var cutoff = DateTime.UtcNow - SightingTtl;
        return macs.Values.Count(t => t >= cutoff) > RotatingIdThreshold;
    }

    /// <summary>
    /// Addresses currently reporting under more than one device id. Ids whose address rotates are
    /// excluded from the comparison - for those the address says nothing about identity.
    /// </summary>
    public IReadOnlyList<SplitIdentity> GetSplitIdentities()
    {
        var cutoff = DateTime.UtcNow - SightingTtl;
        var splits = new List<SplitIdentity>();

        foreach (var (mac, ids) in _byMac)
        {
            var live = ids.Values
                .Where(s => s.LastSeen >= cutoff && !HasRotatingAddress(s.Id))
                .ToList();
            if (live.Count < 2) continue;

            splits.Add(new SplitIdentity
            {
                Mac = mac,
                Ids = live.OrderByDescending(s => s.NodeCount)
                    .Select(s => new SplitIdentityId
                    {
                        Id = s.Id,
                        Nodes = s.Nodes.ToArray(),
                        LastSeen = s.LastSeen
                    })
                    .ToList()
            });
        }

        return splits.OrderByDescending(s => s.Ids.Sum(i => i.Nodes.Length)).ToList();
    }

    /// <summary>Ids whose address rotates - reported so the user knows the address-based check cannot cover them.</summary>
    public IReadOnlyList<string> GetRotatingIds()
    {
        var cutoff = DateTime.UtcNow - SightingTtl;
        return _macsById
            .Where(kv => kv.Value.Values.Count(t => t >= cutoff) > RotatingIdThreshold)
            .Select(kv => kv.Key)
            .OrderBy(id => id)
            .ToList();
    }

    private sealed class IdSighting(string id)
    {
        public string Id { get; } = id;
        public ICollection<string> Nodes => _nodes.Keys;
        public int NodeCount => _nodes.Count;
        public DateTime LastSeen { get; private set; } = DateTime.UtcNow;

        private readonly ConcurrentDictionary<string, byte> _nodes = new(StringComparer.OrdinalIgnoreCase);

        public void Touch(string? nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId)) _nodes[nodeId] = 0;
            LastSeen = DateTime.UtcNow;
        }
    }
}

public class SplitIdentity
{
    public string Mac { get; set; } = "";
    public List<SplitIdentityId> Ids { get; set; } = new();
}

public class SplitIdentityId
{
    public string Id { get; set; } = "";
    public string[] Nodes { get; set; } = Array.Empty<string>();
    public DateTime LastSeen { get; set; }
}
