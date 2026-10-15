using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using PluginSdk;
using PluginSdk.Commands;
using PluginSdk.Config;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRageMath;

namespace ConcealmentProbe;

// Test hooks of the Concealment suite: the plugin's concealed groups, the state of
// grids and blocks on the server, and the plugin config swapped at runtime. Reaches
// Concealment by reflection, so it needs no build dependency on it.
public class Probe : IPlugin
{
    public void Init(object gameInstance) =>
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ProbeCommands));

    public void Update() { }

    public void Dispose() { }
}

[CommandRoot("cprobe", "ConcealmentProbe", "Concealment test hooks")]
public sealed class ProbeCommands : CommandModule
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static Assembly Concealment =>
        AppDomain
            .CurrentDomain.GetAssemblies()
            .First(a => a.GetType("ServerPlugin.ConcealmentManager") != null);

    private static object Plugin =>
        Concealment.GetType("ServerPlugin.Plugin").GetProperty("Instance").GetValue(null);

    private static object Get(object target, string name) =>
        target.GetType().GetProperty(name, Any).GetValue(target);

    private static IEnumerable<MyCubeGrid> ConcealedGrids()
    {
        object manager = Get(Plugin, "Manager");
        foreach (object group in (IList)Get(manager, "ConcealedGroups"))
        foreach (MyCubeGrid grid in (IEnumerable)Get(group, "Grids"))
            yield return grid;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string V(Vector3D v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    private static long Frame => MySession.Static.GameplayFrameCounter;

    [Command("reload", "Loads Concealment.cfg into the running plugin.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Reload()
    {
        object live = Get(Plugin, "ConfigData");
        Type type = live.GetType();
        string path = Path.Combine(MyFileSystem.UserDataPath, "Concealment.cfg");
        object loaded = typeof(ConfigStorage)
            .GetMethod(nameof(ConfigStorage.LoadXml))
            .MakeGenericMethod(type)
            .Invoke(null, new object[] { path });

        foreach (
            PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
            )
        )
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(live, property.GetValue(loaded));
        }

        return "Probe: config reloaded";
    }

    [Command("concealed", "Entity ids of every concealed grid.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Concealed()
    {
        List<long> ids = ConcealedGrids().Select(g => g.EntityId).OrderBy(i => i).ToList();
        return "Probe concealed: " + (ids.Count == 0 ? "none" : string.Join(" ", ids));
    }

    [Command("grid", "A grid on the server: concealment, position, velocity and blocks.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Grid(long gridId)
    {
        if (!(MyEntities.GetEntityById(gridId) is MyCubeGrid grid) || grid.MarkedForClose)
            return "Probe grid: none";

        bool concealed = ConcealedGrids().Any(g => g.EntityId == gridId);
        bool flagged = (grid.Flags & (EntityFlags)4) != 0;
        Vector3D velocity = grid.Physics?.LinearVelocity ?? Vector3D.Zero;
        List<long> group = MyCubeGridGroups
            .Static.Physical.GetGroupNodes(grid)
            .Select(g => g.EntityId)
            .OrderBy(i => i)
            .ToList();
        return $"Probe grid: concealed={(concealed ? 1 : 0)} flagged={(flagged ? 1 : 0)} "
            + $"pos={V(grid.PositionComp.GetPosition())} vel={V(velocity)} "
            + $"blocks={grid.BlocksCount} static={(grid.IsStatic ? 1 : 0)} "
            + $"physics={(grid.Physics?.Enabled == true ? 1 : 0)} group={string.Join(",", group)} frame={Frame}";
    }

    [Command("count", "How many grids have this name, and their entity ids.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Count(string name)
    {
        List<long> ids = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => !g.MarkedForClose && g.DisplayName == name)
            .Select(g => g.EntityId)
            .OrderBy(i => i)
            .ToList();
        return $"Probe count: {ids.Count} {string.Join(" ", ids)}".TrimEnd();
    }

    [Command("ids", "Entity ids of every grid, projections left out.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Ids() =>
        "Probe ids: "
        + string.Join(
            " ",
            MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .Where(g => !g.MarkedForClose && !g.IsPreview)
                .Select(g => g.EntityId)
                .OrderBy(i => i)
        );

    [Command("timer", "A timer block's countdown.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Timer(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyTimerBlock timer))
            return "Probe timer: none";

        var current = (int)
            typeof(MyTimerBlock).GetField("m_countdownMsCurrent", Any).GetValue(timer);
        return $"Probe timer: counting={(((SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock)timer).IsCountingDown ? 1 : 0)} ms={current} frame={Frame}";
    }

    [Command("restarttimer", "Stops a timer block's countdown and starts it again, on the server.")]
    [Permission(MyPromoteLevel.Admin)]
    public string RestartTimer(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is SpaceEngineers.Game.ModAPI.IMyTimerBlock timer))
            return "Probe restarttimer: none";

        // Stopping takes the timer off its 10-tick updates and starting puts it
        // back on, so both change what the block asks updates for
        timer.StopCountdown();
        timer.StartCountdown();
        return "Probe restarttimer: restarted";
    }

    [Command("data", "A terminal block's custom data, newlines as |.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Data(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is IMyTerminalBlock block))
            return "Probe data: none";

        return $"Probe data: {block.CustomData.Replace("\n", "|")} frame={Frame}";
    }

    [Command("production", "A production block's state, input inventory mass in kg and frames since its last production update.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Production(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyProductionBlock block))
            return "Probe production: none";

        return $"Probe production: producing={(block.IsProducing ? 1 : 0)} "
            + $"input={F((double)block.InputInventory.CurrentMass)} "
            + $"output={F((double)block.OutputInventory.CurrentMass)} "
            + $"power={F(block.ResourceSink?.CurrentInputByType(MyResourceDistributorComponent.ElectricityId) ?? 0)} "
            + $"tier={block.CubeGrid.PlayerPresenceTier} since={block.GetFramesFromLastTrigger()} "
            + $"frame={Frame}";
    }

    private static IDictionary BoostBanks =>
        (IDictionary)
            Concealment
                .GetType("ServerPlugin.ProductionBoostManager")
                .GetField("BoostBankFrames", Any)
                .GetValue(null);

    [Command("banks", "The production catch-up banks, in frames per grid.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Banks()
    {
        IDictionary banks = BoostBanks;
        List<string> rows = banks
            .Keys.Cast<long>()
            .OrderBy(k => k)
            .Select(k => $"{k}={F((double)banks[k])}")
            .ToList();
        return "Probe banks: " + (rows.Count == 0 ? "none" : string.Join(" ", rows));
    }

    [Command("clearbanks", "Forgets every production catch-up bank.")]
    [Permission(MyPromoteLevel.Admin)]
    public string ClearBanks()
    {
        BoostBanks.Clear();
        return "Probe clearbanks: done";
    }

    [Command("velocity", "Sets a grid's linear velocity in m/s.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Velocity(long gridId, double x, double y, double z)
    {
        if (!(MyEntities.GetEntityById(gridId) is MyCubeGrid grid) || grid.Physics == null)
            return "Probe velocity: none";

        grid.Physics.LinearVelocity = new Vector3(x, y, z);
        return "Probe velocity: set";
    }

    [Command("ore", "Puts ore into a production block's input inventory.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Ore(long blockId, string subtype, double kg)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyProductionBlock block))
            return "Probe ore: none";

        var ore = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ore>(subtype);
        block.InputInventory.AddItems((MyFixedPoint)kg, ore);
        return $"Probe ore: input={F((double)block.InputInventory.CurrentMass)}";
    }

    [Command("clear", "Empties a production block's inventories.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Clear(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyProductionBlock block))
            return "Probe clear: none";

        block.InputInventory.Clear();
        block.OutputInventory.Clear();
        return "Probe clear: done";
    }

    [Command("thrust", "Sets a thruster's override, 0 to 1 of its maximum.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Thrust(long blockId, float fraction)
    {
        if (!(MyEntities.GetEntityById(blockId) is Sandbox.ModAPI.IMyThrust thruster))
            return "Probe thrust: none";

        thruster.ThrustOverridePercentage = fraction;
        return "Probe thrust: set";
    }

    [Command("place", "Puts a grid at a position, at rest, the way it was built.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Place(long gridId, double x, double y, double z)
    {
        if (!(MyEntities.GetEntityById(gridId) is MyCubeGrid grid))
            return "Probe place: none";

        grid.Teleport(MatrixD.CreateWorld(new Vector3D(x, y, z), Vector3D.Forward, Vector3D.Up));
        if (grid.Physics != null)
        {
            grid.Physics.LinearVelocity = Vector3.Zero;
            grid.Physics.AngularVelocity = Vector3.Zero;
        }

        return "Probe place: done";
    }

    [Command("projector", "A projector's state.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Projector(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyProjectorBase projector))
            return "Probe projector: none";

        return $"Probe projector: enabled={(projector.Enabled ? 1 : 0)} "
            + $"projecting={(projector.ProjectedGrid != null ? 1 : 0)} "
            + $"tier={projector.CubeGrid.PlayerPresenceTier}";
    }

    [Command("updating", "Whether the game updates an entity, once its queued update changes apply.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Updating(long entityId)
    {
        if (!(MyEntities.GetEntityById(entityId) is MyEntity entity))
            return "Probe updating: none";

        object orchestrator = MyEntities.Orchestrator;
        object Field(string name) => orchestrator.GetType().GetField(name, Any).GetValue(orchestrator);
        bool Queued(string name) => ((IEnumerable<MyEntity>)Field(name)).Contains(entity);

        bool updating =
            Queued("m_entitiesToAdd")
            || (((IDictionary)Field("m_lastUpdateRecord")).Contains(entity) && !Queued("m_entitiesToRemove"));
        return $"Probe updating: {(updating ? 1 : 0)}";
    }

    [Command("sensor", "A sensor's state and the entity ids it detects.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Sensor(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is Sandbox.ModAPI.Ingame.IMySensorBlock sensor))
            return "Probe sensor: none";

        var detected = new List<Sandbox.ModAPI.Ingame.MyDetectedEntityInfo>();
        sensor.DetectedEntities(detected);
        return $"Probe sensor: active={(sensor.IsActive ? 1 : 0)} "
            + $"detected={string.Join(",", detected.Select(d => d.EntityId).OrderBy(i => i))}";
    }

    [Command("attach", "Builds the top part of a rotor or piston base that has none.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Attach(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyMechanicalConnectionBlockBase stator))
            return "Probe attach: none";

        if (stator.TopGrid == null)
            typeof(MyMechanicalConnectionBlockBase)
                .GetMethod("RecreateTop", Any)
                .Invoke(
                    stator,
                    new object[]
                    {
                        stator.OwnerId,
                        MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal,
                        true,
                    }
                );

        return $"Probe attach: top={stator.TopGrid?.EntityId ?? 0}";
    }

    [Command("connect", "Locks a connector to the one in front of it.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Connect(long blockId)
    {
        if (
            !(MyEntities.GetEntityById(blockId) is Sandbox.ModAPI.Ingame.IMyShipConnector connector)
        )
            return "Probe connect: none";

        connector.Connect();
        return $"Probe connect: status={connector.Status}";
    }

    [Command("action", "Applies a terminal action to a block on the server.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Action(long blockId, string action)
    {
        if (!(MyEntities.GetEntityById(blockId) is Sandbox.ModAPI.IMyTerminalBlock block))
            return "Probe action: none";

        var terminalAction = block.GetActionWithName(action);
        if (terminalAction == null)
            return "Probe action: unknown";

        terminalAction.Apply(block);
        return "Probe action: applied";
    }

    [Command(
        "tp",
        "Teleports a player the way the admin teleport does, for players without the right."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Teleport(ulong steamId, double x, double y, double z)
    {
        typeof(MyMultiplayer)
            .GetMethod("OnTeleport", Any)
            .Invoke(null, new object[] { steamId, new Vector3D(x, y, z) });
        return "Probe tp: done";
    }

    [Command("players", "Online players and their positions.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Players()
    {
        var players = new List<IMyPlayer>();
        MyAPIGateway.Players.GetPlayers(players);
        return "Probe players: "
            + string.Join(
                " ",
                players
                    .OrderBy(p => p.SteamUserId)
                    .Select(p => $"{p.SteamUserId}={V(p.GetPosition())}")
            );
    }

    [Command("save", "Saves the world.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Save() => $"Probe save: {ServerControl.SaveWorld()}";

    [Command("frame", "The server's simulation frame and the plugin's tick.")]
    [Permission(MyPromoteLevel.Admin)]
    public string FrameCommand() => $"Probe frame: frame={Frame} tick={Get(Plugin, "Tick")}";
}
