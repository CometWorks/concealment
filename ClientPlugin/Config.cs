using System;
using System.Collections.Generic;
using ClientPlugin.Settings.Elements;
using Shared.Config;

namespace ClientPlugin;

public class Config
{
    public static readonly Config Current = new Config();
    public readonly string Title = "Concealment - local and hosted worlds";
    private static PluginConfig Data => Plugin.Instance.ConfigData;

    [Checkbox(description: "Enable periodic concealment in worlds you host")]
    public bool Enabled
    {
        get => Data.Enabled;
        set => Data.Enabled = value;
    }

    [Slider(0, 10000000, step: 100, unit: "m")]
    public float ConcealDistance
    {
        get => (float)Data.ConcealDistance;
        set => Data.ConcealDistance = value;
    }

    [Slider(1, 864000, type: SliderAttribute.SliderType.Integer, unit: "ticks")]
    public int ConcealInterval
    {
        get => Data.ConcealInterval;
        set => Data.ConcealInterval = value;
    }

    [Slider(0, 10000000, step: 100, unit: "m")]
    public float RevealDistance
    {
        get => (float)Data.RevealDistance;
        set => Data.RevealDistance = value;
    }

    [Slider(1, 864000, type: SliderAttribute.SliderType.Integer, unit: "ticks")]
    public int RevealInterval
    {
        get => Data.RevealInterval;
        set => Data.RevealInterval = value;
    }

    [Dropdown(description: "Off keeps active production revealed; Conceal pauses it; Approximate banks catch-up")]
    public ProductionConcealmentMode ProductionConcealment
    {
        get => Data.ProductionConcealment;
        set => Data.ProductionConcealment = value;
    }

    [Slider(1, 1000, step: 0.5f)]
    public float ProductionBoostLevel
    {
        get => (float)Data.ProductionBoostLevel;
        set => Data.ProductionBoostLevel = value;
    }

    [Slider(0, 168, step: 0.5f, unit: "hours")]
    public float MaxBoostHours
    {
        get => (float)Data.MaxBoostHours;
        set => Data.MaxBoostHours = value;
    }

    [Checkbox]
    public bool ConcealPirates
    {
        get => Data.ConcealPirates;
        set => Data.ConcealPirates = value;
    }

    [Checkbox(description: "Register the remote-control keep-alive action next time you load a world")]
    public bool RemoteControlKeepAliveAction
    {
        get => Data.RemoteControlKeepAliveAction;
        set => Data.RemoteControlKeepAliveAction = value;
    }

    [Slider(1, 3600, unit: "s")]
    public float DynamicConcealQueryInterval
    {
        get => (float)Data.DynamicConcealQueryInterval;
        set => Data.DynamicConcealQueryInterval = value;
    }

    [Slider(0.1f, 3600, step: 0.1f, unit: "s")]
    public float DynamicConcealScanInterval
    {
        get => (float)Data.DynamicConcealScanInterval;
        set => Data.DynamicConcealScanInterval = value;
    }

    [Textbox(description: "Comma-separated block subtypes that keep their grids revealed")]
    public string ExcludedSubtypes
    {
        get => string.Join(", ", Data.ExcludedSubtypes ?? new List<string>());
        set => Data.ExcludedSubtypes = new List<string>((value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)).ConvertAll(s => s.Trim());
    }

    [Button(label: "Reveal all grids")]
    public static void RevealAll() => Plugin.Instance.RunCommand("!conceal reveal all");

    [Button(label: "Run concealment")]
    public static void Run() => Plugin.Instance.RunCommand("!conceal run");
}
