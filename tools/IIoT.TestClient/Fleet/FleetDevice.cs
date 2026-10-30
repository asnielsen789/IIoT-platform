namespace IIoT.TestClient.Fleet;

/// <summary>What kind of device a fleet entry simulates (ADR-011).</summary>
public enum DeviceProfile
{
    /// <summary>A storage tank: one level.</summary>
    Tank,

    /// <summary>An EV charging station: cumulative energy, power, and a cumulative count of sessions.</summary>
    Charger,

    /// <summary>A car wash: cumulative water and wash cycles, and the levels of its shampoo, wax and reclaim tanks.</summary>
    CarWash,

    /// <summary>An oil separator: the oil layer and the sludge.</summary>
    OilSeparator
}

/// <summary>One device of a simulated fleet.</summary>
/// <param name="Id">Its device ID, which is also its DPS registration ID and its certificate's CN.</param>
/// <param name="Profile">What it simulates.</param>
/// <param name="Site">Where it stands, for people reading the fleet file. Not sent: a static
/// attribute belongs in the device twin, not in telemetry (docs/mqtt-payload.md, section 3).</param>
public sealed record FleetDevice(string Id, DeviceProfile Profile, string Site)
{
    /// <summary>
    /// The seed of the device's simulation, derived from its ID: the same device behaves the same
    /// way on every run and every machine, and no two devices behave alike.
    /// </summary>
    /// <remarks>
    /// FNV-1a over the ID's UTF-16 code units. Not <see cref="string.GetHashCode()"/>, which .NET
    /// randomises per process.
    /// </remarks>
    public int Seed
    {
        get
        {
            var hash = 2166136261u;
            foreach (var character in Id)
            {
                hash = (hash ^ character) * 16777619u;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }

    /// <summary>The firmware version the profile reports, one per profile.</summary>
    public string Firmware => Profile switch
    {
        DeviceProfile.Tank => "1.4.2",
        DeviceProfile.Charger => "2.1.0",
        DeviceProfile.CarWash => "1.7.3",
        _ => "1.0.4"
    };

    /// <summary>Whether the device runs on a battery, and so reports its voltage. Chargers and car washes are mains-powered.</summary>
    public bool OnBattery => Profile is DeviceProfile.Tank or DeviceProfile.OilSeparator;
}
