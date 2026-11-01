using System.Collections.Generic;
using System.Xml.Serialization;
#if MAGNETAR
using PluginSdk.Config;
#else
using System.ComponentModel;
using System.Runtime.CompilerServices;
#endif

namespace Shared.Config;

public enum DynamicConcealType
{
    HostileCharacters = 0,
    NeutralCharacters,
    FriendlyCharacters,
    HostileGrids,
    NeutralGrids,
    None,
}

public enum ProductionConcealmentMode
{
#if MAGNETAR
    [EnumCaption("Off - keep active production revealed")]
#endif
    Off = 0,

#if MAGNETAR
    [EnumCaption("Conceal - production pauses while hidden")]
#endif
    Conceal,

#if MAGNETAR
    [EnumCaption("Approximate - catch up after reveal")]
#endif
    Approximate,
}

public struct DynamicConcealmentRule
{
#if MAGNETAR
    [StructMember("Displayed rule name")]
    [StructCaption]
#endif
    public string Name { get; set; }

#if MAGNETAR
    [StructMember(
        "Object builder type, without the MyObjectBuilder_ prefix. Example: LargeGatlingTurret"
    )]
#endif
    public string TargetType { get; set; }

#if MAGNETAR
    [StructMember("Subtype to target. Leave empty to target every subtype of the type.")]
#endif
    public string TargetSubtype { get; set; }

#if MAGNETAR
    [StructMember("Nearby entity relation that reveals the block update")]
#endif
    public DynamicConcealType ConcealType { get; set; }

#if MAGNETAR
    [StructMember("Distance in meters")]
#endif
    public double Distance { get; set; }
}

#if MAGNETAR
[Tab("general", caption: "General")]
[Tab("timing", caption: "Timing")]
[Tab("rules", caption: "Rules")]
[Tab("dynamic", caption: "Dynamic")]
[Section("dynamic-timing", parent: "dynamic", caption: "Timing")]
[Section("dynamic-rules", parent: "dynamic", caption: "Rules")]
#endif
[XmlRoot("Settings")]
#if MAGNETAR
public class PluginConfig : PluginSdk.Config.PluginConfig, IPluginConfig
#else
public class PluginConfig : IPluginConfig
#endif
{
#if !MAGNETAR
    public event PropertyChangedEventHandler PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
#endif

#if MAGNETAR
    [BoolOption("Enable periodic concealment", Parent = "general")]
#endif
    public bool Enabled
    {
        get;
        set => SetField(ref field, value);
    } = true;

#if MAGNETAR
    [DoubleOption(
        0,
        10000000,
        "Hide eligible grid groups when every online player is farther away than this many meters.",
        Parent = "timing"
    )]
#endif
    public double ConcealDistance
    {
        get;
        set => SetField(ref field, value);
    } = 15000;

#if MAGNETAR
    [IntOption(
        1,
        864000,
        "Simulation ticks between scans that hide eligible grids. At 60 TPS, 18000 ticks is 5 minutes.",
        Parent = "timing"
    )]
#endif
    public int ConcealInterval
    {
        get;
        set => SetField(ref field, value);
    } = 18000;

#if MAGNETAR
    [DoubleOption(
        0,
        10000000,
        "Reveal concealed grid groups when an online player is within this many meters.",
        Parent = "timing"
    )]
#endif
    public double RevealDistance
    {
        get;
        set => SetField(ref field, value);
    } = 12000;

#if MAGNETAR
    [IntOption(
        1,
        864000,
        "Simulation ticks between scans that reveal hidden grids near players.",
        Parent = "timing"
    )]
#endif
    public int RevealInterval
    {
        get;
        set => SetField(ref field, value);
    } = 60;

#if MAGNETAR
    [EnumOption(
        "Active assembler/refinery handling. Off keeps active production grids revealed. Conceal hides them and production pauses. Approximate hides them, then banks boost time on reveal so they catch up at higher speed while online.",
        Parent = "rules"
    )]
#endif
    public ProductionConcealmentMode ProductionConcealment
    {
        get;
        set => SetField(ref field, value);
    } = ProductionConcealmentMode.Approximate;

#if MAGNETAR
    [DoubleOption(
        1.0,
        1000.0,
        "Approximate catch-up speed. Production runs this many times faster while boosting, drawing this many times more power. Concealed time divided by this level is how long the boost lasts online.",
        Parent = "rules"
    )]
#endif
    public double ProductionBoostLevel
    {
        get;
        set => SetField(ref field, value);
    } = 5.0;

#if MAGNETAR
    [DoubleOption(
        0.0,
        168.0,
        "Maximum banked boost time in hours of online catch-up. Boost banked above this limit is lost, capping how much catch-up can accumulate while concealed.",
        Parent = "rules"
    )]
#endif
    public double MaxBoostHours
    {
        get;
        set => SetField(ref field, value);
    } = 8.0;

#if MAGNETAR
    [BoolOption("Allow pirate-owned grids to be concealed", Parent = "rules")]
#endif
    public bool ConcealPirates
    {
        get;
        set => SetField(ref field, value);
    } = true;

#if MAGNETAR
    [BoolOption(
        "Register a remote control terminal action that temporarily keeps a grid revealed",
        Parent = "rules"
    )]
#endif
    public bool RemoteControlKeepAliveAction
    {
        get;
        set => SetField(ref field, value);
    }

#if MAGNETAR
    [ListOption(
        description: "Block subtypes that keep an entire physical grid group revealed",
        Parent = "rules"
    )]
#endif
    public List<string> ExcludedSubtypes
    {
        get;
        set => SetField(ref field, value);
    } = new List<string>();

#if MAGNETAR
    [DoubleOption(
        1,
        3600,
        "Seconds between expensive nearby-entity queries for dynamic concealment",
        Parent = "dynamic-timing"
    )]
#endif
    public double DynamicConcealQueryInterval
    {
        get;
        set => SetField(ref field, value);
    } = 15;

#if MAGNETAR
    [DoubleOption(
        0.1,
        3600,
        "Seconds between per-block dynamic concealment state refreshes",
        Parent = "dynamic-timing"
    )]
#endif
    public double DynamicConcealScanInterval
    {
        get;
        set => SetField(ref field, value);
    } = 2;

#if MAGNETAR
    [ListOption(
        description: "Rules that skip selected block updates while no configured entities are nearby",
        Parent = "dynamic-rules"
    )]
#endif
    public List<DynamicConcealmentRule> DynamicConcealment
    {
        get;
        set => SetField(ref field, value);
    } = new List<DynamicConcealmentRule>();
}
