using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Splitting one piece of hardware across several device ids costs measurements silently - both ids
/// look healthy on their own, and on this installation the split was worth 20 % of all readings
/// before anyone noticed. The detector had no tests at all, which is uncomfortable for a check whose
/// entire value is catching something invisible.
///
/// The rotating-address case is the one that shaped the design: phones and watches change their
/// address on purpose, so for them a shared address means nothing and an unqualified
/// "same address, therefore same device" rule would report the whole house as split.
/// </summary>
public class DeviceIdentityTrackerTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private DeviceIdentityTracker _sut = null!;

    [SetUp]
    public void Setup()
    {
        _mqtt = new Mock<IMqttCoordinator>();
        _sut = new DeviceIdentityTracker(_mqtt.Object);
    }

    private void Seen(string deviceId, string mac, string nodeId = "floor")
    {
        _mqtt.Raise(m => m.DeviceMessageReceivedAsync += null,
            new DeviceMessageEventArgs
            {
                DeviceId = deviceId,
                NodeId = nodeId,
                Payload = new DeviceMessage { Mac = mac }
            });
    }

    [Test]
    public void ReportsOneAddressSeenUnderTwoIds()
    {
        Seen("ibeacon-osirisx", "aabbccddeeff", "kitchen");
        Seen("ibeacon-osirisx", "aabbccddeeff", "floor");
        Seen("irk:0123", "aabbccddeeff", "living_room");

        var splits = _sut.GetSplitIdentities();

        Assert.That(splits, Has.Count.EqualTo(1));
        Assert.That(splits[0].Mac, Is.EqualTo("aabbccddeeff"));
        Assert.That(splits[0].Ids.Select(i => i.Id), Is.EquivalentTo(new[] { "ibeacon-osirisx", "irk:0123" }));
    }

    [Test]
    public void RanksTheIdHeardByMostNodesFirst()
    {
        // The message tells the user which id to alias the others onto, so the order is not
        // cosmetic - picking the id fewest nodes know would lose more measurements than it saves.
        Seen("sparse", "aabbccddeeff", "kitchen");
        Seen("rich", "aabbccddeeff", "kitchen");
        Seen("rich", "aabbccddeeff", "floor");
        Seen("rich", "aabbccddeeff", "living_room");

        Assert.That(_sut.GetSplitIdentities()[0].Ids.First().Id, Is.EqualTo("rich"));
    }

    [Test]
    public void SaysNothingAboutAnAddressWithASingleId()
    {
        Seen("only-me", "aabbccddeeff", "kitchen");
        Seen("only-me", "aabbccddeeff", "floor");

        Assert.That(_sut.GetSplitIdentities(), Is.Empty);
    }

    [Test]
    public void TreatsAnIdSeenUnderManyAddressesAsRotating()
    {
        // A phone. Three addresses inside the window, which is past the threshold of two.
        Seen("phone", "aa00000000001");
        Seen("phone", "aa00000000002");
        Seen("phone", "aa00000000003");

        Assert.That(_sut.HasRotatingAddress("phone"), Is.True);
        Assert.That(_sut.GetRotatingIds(), Does.Contain("phone"));
    }

    [Test]
    public void ASingleRotationDoesNotCountAsRotating()
    {
        // Two addresses can happen to a static device through a firmware quirk or a re-pairing.
        // Treating that as rotating would remove it from the split check, which is where the real
        // finding lives - the threshold exists to keep the check useful, not to be cautious.
        Seen("beacon", "aa00000000001");
        Seen("beacon", "aa00000000002");

        Assert.That(_sut.HasRotatingAddress("beacon"), Is.False);
    }

    [Test]
    public void KeepsRotatingIdsOutOfTheSplitReport()
    {
        // Two phones passing the same rotated address around would otherwise be reported as one
        // device split in two - a finding that is not only wrong but unfixable, since the suggested
        // remedy is an alias that would merge two different people.
        foreach (var mac in new[] { "aa01", "aa02", "aa03", "shared" }) Seen("phone-a", mac);
        foreach (var mac in new[] { "bb01", "bb02", "bb03", "shared" }) Seen("phone-b", mac);

        Assert.That(_sut.GetSplitIdentities(), Is.Empty);
        Assert.That(_sut.GetRotatingIds(), Is.EquivalentTo(new[] { "phone-a", "phone-b" }));
    }

    [Test]
    public void AStaticDeviceSharingAnAddressWithAPhoneIsStillReported()
    {
        // Only the rotating side is excluded. If a real split hides behind one phone, dropping the
        // whole address from the comparison would hide it too.
        foreach (var mac in new[] { "aa01", "aa02", "aa03", "shared" }) Seen("phone", mac);
        Seen("beacon-a", "shared");
        Seen("beacon-b", "shared");

        var splits = _sut.GetSplitIdentities();

        Assert.That(splits, Has.Count.EqualTo(1));
        Assert.That(splits[0].Ids.Select(i => i.Id), Is.EquivalentTo(new[] { "beacon-a", "beacon-b" }));
    }

    [Test]
    public void IgnoresMessagesWithoutAnAddress()
    {
        // Nodes on older firmware send no mac at all. Recording those under a null key would group
        // every such device together and report the lot as one giant split.
        _mqtt.Raise(m => m.DeviceMessageReceivedAsync += null,
            new DeviceMessageEventArgs { DeviceId = "a", NodeId = "floor", Payload = new DeviceMessage { Mac = null } });
        _mqtt.Raise(m => m.DeviceMessageReceivedAsync += null,
            new DeviceMessageEventArgs { DeviceId = "b", NodeId = "floor", Payload = new DeviceMessage { Mac = "" } });

        Assert.That(_sut.GetSplitIdentities(), Is.Empty);
    }
}
