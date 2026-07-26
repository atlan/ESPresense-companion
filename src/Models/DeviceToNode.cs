namespace ESPresense.Models;

public class DeviceToNode(Device device, Node node)
{
    public Device Device { get; } = device;
    public Node Node { get; } = node;

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    public double Rssi { get; set; }
    public double? RssiVar { get; set; }

    /// <summary>
    /// Receive adjustment the rx node applied when it reported this reading. The node publishes it
    /// alongside the raw level and it was being dropped here - without it a level cannot be
    /// normalised for node sensitivity, which spans -5..+25 dB across a fleet, so a comparison of
    /// levels from different nodes would mostly compare the nodes.
    /// </summary>
    public double? RssiRxAdj { get; set; }
    public double RefRssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < Device!.Timeout;

    public bool ReadMessage(DeviceMessage payload)
    {
        Rssi = payload.Rssi;
        RssiVar = payload.RssiVar;
        RefRssi = payload.RefRssi;
        RssiRxAdj = payload.RssiRxAdj;
        NewName(payload.Name);
        var moved = Math.Abs(LastDistance - payload.Distance) > 0.25;
        if (moved) LastDistance = payload.Distance;
        Distance = payload.Distance;
        DistVar = payload.DistVar;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }

    private void NewName(string? name)
    {
        if (Device == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (Device.Name == name) return;
        Device.Name = name;
        Device.Check = true;
    }
}