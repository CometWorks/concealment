using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using PluginSdk;
using PluginSdk.Clustering;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders.Private;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRageMath;
using SpaceEngineers.Game.Entities.Blocks.SafeZone;

namespace ExtractionProbe;

// Optional, isolated native test driver. No production route or test switch.
public sealed partial class Probe : IPlugin
{
    private string root, last;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private static Type PluginType => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ServerPlugin.Plugin", false))
        .Single(t => t?.GetField("Name")?.GetRawConstantValue() as string == "Concealment");
    private static object Plugin => PluginType.GetProperty("Instance").GetValue(null);
    private static object Runtime => PluginType.Assembly.GetType("Shared.Concealment.ConcealmentRuntime").GetProperty("Instance").GetValue(null);
    private static object Manager => Runtime?.GetType().GetProperty("Manager").GetValue(Runtime);
    private static object Config => Runtime?.GetType().GetProperty("ConfigData").GetValue(Runtime) ?? Plugin?.GetType().GetProperty("ConfigData").GetValue(Plugin);
    public void Init(object gameInstance)
    {
        root = Environment.GetEnvironmentVariable("EXTRACTION_PROBE_ROOT"); Directory.CreateDirectory(root);
        string reference = Environment.GetEnvironmentVariable("EXTRACTION_CLUSTER_REFERENCE");
        if (!string.IsNullOrEmpty(reference))
        {
            // Isolated native references stage definition-only mods. Install only
            // the existing local-mod loader, leaving Cluster lifecycle/clocks dormant.
            var assembly = Assembly.LoadFrom(reference);
            new Harmony("ExtractionReference.local-mods").CreateClassProcessor(assembly.GetType("ClusterPlugin.LocalWorldModsPatch")).Patch();
        }
    }
    public void Dispose() { }
    public void Update()
    {
        if (MySession.Static?.Ready != true || root == null) return;
        try
        {
            ObserveClockReference();
            var command = Path.Combine(root, "command.json");
            if (!File.Exists(command)) return;
            string raw = File.ReadAllText(command);
            if (raw == last) return;
            using var doc = JsonDocument.Parse(raw);
            var request = doc.RootElement;
            string id = request.GetProperty("id").GetString();
            last = raw;
            object value;
            try { value = Execute(request); Write("reply-" + id + ".json", new { success = true, frame = MySession.Static.GameplayFrameCounter, elapsed = MySession.Static.ElapsedGameTime.Ticks, value }); }
            catch (Exception e) { Write("reply-" + id + ".json", new { success = false, error = e.ToString() }); }
        }
        catch (Exception e) { Write("error.json", new { error = e.ToString() }); }
    }
    private object Execute(JsonElement r)
    {
        switch (r.GetProperty("action").GetString())
        {
            case "inspect": return Inspect();
            case "explicit-core": return ExplicitCore();
            case "config":
                foreach (var option in r.GetProperty("values").EnumerateObject())
                {
                    var p = Config.GetType().GetProperty(option.Name) ?? throw new ArgumentException("Unknown config " + option.Name);
                    object value = p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, option.Value.GetString()) : JsonSerializer.Deserialize(option.Value.GetRawText(), p.PropertyType);
                    p.SetValue(Config, value);
                }
                return Inspect();
            case "fixtures": return Fixtures();
            case "recovery-fixtures": return RecoveryFixtures();
            case "manager-failure": return ArmManagerFailure();
            case "admission": return RecoveryAdmission(r.GetProperty("name").GetString());
            case "world-unload":
                MySession.Static.Unload();
                return new { runtime = Runtime != null, manager = Manager != null,
                    patches = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m).Owners.Contains("Concealment.Server")).Select(m => m.ToString()).ToArray() };
            case "clock-reference": return BeginClockReference(r);
            case "clock-model": return ClockModel(r);
            case "clock-content-ready": return new { ready = (bool)PluginType.Assembly.GetType("Concealment.Accounting.AccountingApi").GetMethod("ContentReady").Invoke(null, null) };
            case "cluster-reference-host": return ClusterReferenceHost();
            case "factory-fixture": return FactoryFixture();
            case "environment": return EnvironmentFixtures();
            case "detached-commit": return DetachedCommitChecks();
            case "shift":
                var shifted = Named(r.GetProperty("name").GetString());
                var pose = shifted.WorldMatrix; pose.Translation += new Vector3D(.001, 0, 0); shifted.WorldMatrix = pose;
                return Inspect();
            case "engine-capacity":
                Named(r.GetProperty("name").GetString()).GetFatBlocks<SpaceEngineers.Game.Entities.Blocks.MyGasFueledPowerProducer>().Single().Capacity = r.GetProperty("capacity").GetSingle();
                return Inspect();
            case "split":
                var original = Named(r.GetProperty("name").GetString());
                var part = original.GetBlocks().Where(b => b.FatBlock is MyRefinery).ToList();
                var child = MyCubeGrid.CreateSplit(original, part, false);
                if (child != null) child.DisplayName = "extract-split";
                return Inspect();
            case "merge":
                var survivor = Named(r.GetProperty("survivor").GetString());
                var removed = Named(r.GetProperty("removed").GetString());
                survivor.MergeGrid_MergeBlock(removed, new Vector3I(20, 0, 0), false);
                return Inspect();
            case "conceal": return Manager?.GetType().GetMethod("ConcealGrids").Invoke(Manager, new object[] { 15000.0 });
            case "reveal": var predicted = Prediction(); var count = Manager?.GetType().GetMethod("RevealAll").Invoke(Manager, null); return new { predicted, count, state = Inspect() };
            case "save": return new { saved = ServerControl.SaveWorld(), path = MySession.Static.CurrentPath, userData = MyFileSystem.UserDataPath };
            case "dispose": PluginType.GetMethod("Dispose").Invoke(Plugin, null); return Inspect();
            case "mutate":
                var grid = MyEntities.GetEntities().OfType<MyCubeGrid>().Single(g => g.DisplayName == r.GetProperty("name").GetString());
                grid.GetFatBlocks<MyRefinery>().Single().InputInventory.AddItems(1, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Iron")); return Inspect();
            case "commands":
                var beforeCommands = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName.StartsWith("extract-")).OrderBy(g => g.EntityId).Select(State).ToArray();
                var type = PluginType.Assembly.GetType("ServerPlugin.ConcealmentCommands");
                var module = Activator.CreateInstance(type);
                var replies = new[] { "Enable", "Disable", "RevealAll", "Conceal" }.Select(n =>
                {
                    var method = type.GetMethod(n);
                    return new { command = n, result = method.Invoke(module, n == "Conceal" ? new object[] { 15000.0 } : null)?.ToString() };
                }).ToArray();
                return new { before = beforeCommands, replies, after = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName.StartsWith("extract-")).OrderBy(g => g.EntityId).Select(State).ToArray() };
            default: throw new ArgumentException("Unknown probe action");
        }
    }
    private static MyCubeGrid Named(string name) => MyEntities.GetEntities().OfType<MyCubeGrid>().Single(g => g.DisplayName == name);
    private static object Inspect()
    {
        var engine = PluginType.Assembly.GetType("Concealment.Accounting.AccountingEngine");
        var censuses = engine.GetField("CensusByGroup", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as System.Collections.IDictionary;
        object accounting = Manager?.GetType().GetProperty("Accounting", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Manager);
        var records = accounting?.GetType().GetField("records", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(accounting) as System.Collections.IDictionary;
        object production = Manager?.GetType().GetProperty("Production")?.GetValue(Manager);
        var legacyType = PluginType.Assembly.GetType("Shared.Concealment.LegacyBoostMigration");
        var host = PluginType.Assembly.GetType("Concealment.Accounting.Host");
        var hostOwner = PluginType.Assembly.GetType("Concealment.Accounting.AccountingApi").GetField("owner", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        return new
        {
            clusterMode = PluginType.GetProperty("ClusterMode").GetValue(null), manager = Manager != null,
            tick = Runtime?.GetType().GetProperty("Tick").GetValue(Runtime), config = Config,
            runtime = Runtime != null,
            failed = Runtime?.GetType().GetField("failed", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Runtime),
            disposed = Runtime?.GetType().GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Runtime),
            scheduling = PluginType.Assembly.GetType("Shared.Concealment.ConcealmentRuntime").GetProperty("StandaloneActive", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null),
            world = MySession.Static.WorldId, path = MySession.Static.CurrentPath, userData = MyFileSystem.UserDataPath,
            quarantinedCount = (accounting?.GetType().GetField("opaque", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(accounting) as System.Collections.IDictionary)?.Count,
            censusCount = censuses?.Count, claims = records?.Values.Cast<object>().Distinct().Select(v => JsonSerializer.SerializeToElement(v, Json)).ToArray(),
            claimRows = records?.Keys.Cast<long>().Select(id => new { id, claim = JsonSerializer.SerializeToElement(records[id], Json) }).ToArray(),
            opaqueRows = OpaqueRows(accounting),
            coreAssembly = engine.Assembly.FullName, coreModule = engine.Assembly.ManifestModule.ModuleVersionId,
            coreCopies = AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetType("Concealment.Accounting.AccountingApi", false) != null),
            oldGrantController = PluginType.Assembly.GetType("Shared.Concealment.ProductionBoostManager", false) != null,
            hostOwner,
            hostCallbackCount = host.GetFields(BindingFlags.NonPublic | BindingFlags.Static).Count(f => typeof(Delegate).IsAssignableFrom(f.FieldType) && f.GetValue(null) != null),
            accountingClock = hostOwner == null ? null : new {
                frame = engine.GetProperty("Frame", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null),
                maxFrames = engine.GetProperty("MaxFrames", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null),
                resourceFrameSeconds = host.GetField("ClockFrameSeconds", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null),
            },
            provider = ClusterNodeLink.Current?.GetType().AssemblyQualifiedName,
            providerConnected = ClusterNodeLink.Current?.IsConnected,
            legacy = production == null ? null : new {
                ready = legacyType.GetProperty("Ready", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(production),
                receipts = (legacyType.GetField("receipts", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(production) as System.Collections.IDictionary)?.Values.Cast<object>().Select(v => JsonSerializer.SerializeToElement(v, Json)).ToArray(),
                banks = legacyType.GetField("BoostBankFrames", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null),
                opaque = legacyType.GetField("opaque", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(production),
            },
            patches = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m).Owners.Any(o => o == "Concealment" || o.StartsWith("Concealment."))).Select(m => m.DeclaringType.FullName + "." + m.Name).ToArray(),
            grids = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName.StartsWith("extract-")).OrderBy(g => g.DisplayName).Select(State).ToArray(),
        };
    }
    private static object ExplicitCore()
    {
        var api = PluginType.Assembly.GetType("Concealment.Accounting.AccountingApi");
        var callbacks = new Dictionary<string, object>
        {
            ["clockFrameSeconds"] = 1.0 / 60,
            ["clock"] = (Func<double>)(() => MySession.Static.ElapsedGameTime.TotalMilliseconds),
            ["setting"] = (Func<string,string>)(key => key switch {
                "offline.compensate" => "true", "offline.compensateMaxHours" => "72",
                "offline.settleFrames" => "300", "offline.observeSeconds" => "180",
                "offline.rotateProduction" => "true", "offline.solarSteps" => "72",
                "offline.cacheTolerance" => "0.02", "offline.unknownBlocks" => "Freeze",
                "offline.longObservation" => "Skip", "offline.quietMeters" => "15000",
                _ => throw new ArgumentException(key) }),
            ["admit"] = (Func<ulong,uint,bool>)((_,_) => true),
            ["generation"] = (Func<ulong,uint>)(_ => 1),
            ["observe"] = (Func<bool>)(() => true),
            ["frozen"] = (Func<long,bool>)(_ => false),
            ["measure"] = (Func<MyCubeGrid[],bool>)(_ => true),
            ["zonePending"] = (Func<MySafeZoneBlock,bool>)(_ => false),
        };
        api.GetMethod("Configure").Invoke(null, new object[] { "Cluster", callbacks });
        var grids = new[] { MyEntities.GetEntities().OfType<MyCubeGrid>().Single(g => g.DisplayName == "extract-zero") };
        ulong scope = unchecked((ulong)grids[0].EntityId);
        bool ready = (bool)api.GetMethod("Prepare").Invoke(null, new object[] { scope, new[] { grids }, grids, false });
        byte[] profile = ready ? (byte[])api.GetMethod("Capture").Invoke(null, new object[] { scope, new[] { grids }, grids, false }) : null;
        return new { ready, profile = profile == null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(profile), state = Inspect() };
    }
    private static object Prediction()
    {
        object accounting = Manager?.GetType().GetProperty("Accounting", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(Manager);
        var records = accounting?.GetType().GetField("records", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(accounting) as System.Collections.IDictionary;
        var model = PluginType.Assembly.GetType("Concealment.Accounting.AccountingApi").GetMethod("Model");
        long wake = (long)Math.Floor(MySession.Static.ElapsedGameTime.TotalMilliseconds / 16);
        long cap = (int)Config.GetType().GetProperty("OfflineCompensationMaxHours").GetValue(Config) * (3600000L / 16);
        return records?.Values.Cast<object>().Distinct().SelectMany(record =>
        {
            var json = JsonSerializer.SerializeToElement(record, Json);
            if (!json.GetProperty("Concealed").GetBoolean() || json.GetProperty("Profiles").ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
            return json.GetProperty("Profiles").EnumerateArray().Select(profile =>
            {
                byte[] result = (byte[])model.Invoke(null, new object[] { System.Text.Encoding.UTF8.GetBytes(profile.GetRawText()), wake, cap });
                return JsonSerializer.Deserialize<JsonElement>(result);
            }).ToArray();
        }).ToArray();
    }
    private static object State(MyCubeGrid g) => new
    {
        name = g.DisplayName, id = g.EntityId, concealed = ((int)g.Flags & 4) != 0,
        physicalMembers = MyCubeGridGroups.Static.Physical.GetGroup(g)?.Nodes.Select(n => n.NodeData.EntityId).Order().ToArray(),
        logicalMembers = MyCubeGridGroups.Static.Logical.GetGroup(g)?.Nodes.Select(n => n.NodeData.EntityId).Order().ToArray(),
        matrix = g.WorldMatrix.ToString(),
        refinery = g.GetFatBlocks<MyRefinery>().Select(b => new { block = b.EntityId, input = (decimal)b.InputInventory.GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Ore/Iron")), output = (decimal)b.OutputInventory.GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Ingot/Iron")), b.IsProducing }).ToArray(),
        fuel = g.GetFatBlocks<MyReactor>().Select(b => new { block = b.EntityId, uranium = (decimal)b.GetInventory().GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Ingot/Uranium")), output = b.SourceComp.CurrentOutput, period = b.m_timer.TimerTickInFrames, phase = b.m_timer.FramesFromLastTrigger }).ToArray(),
        battery = g.GetFatBlocks<MyBatteryBlock>().Select(b => new { block = b.EntityId, stored = b.CurrentStoredPower }).ToArray(),
        environment = EnvironmentState(g),
        blockFlags = g.GetFatBlocks().Select(b => new { block = b.EntityId, concealed = ((int)b.Flags & 4) != 0 }).ToArray(),
    };
    private static object Fixtures()
    {
        foreach (var variant in new[] { "powered", "control", "zero", "limited" })
        {
            var b = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
            b.GridSizeEnum = MyCubeSize.Large; b.IsStatic = true; b.CreatePhysics = true;
            b.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
            b.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(30000 + Array.IndexOf(new[] { "powered", "control", "zero", "limited" }, variant) * 2000, 30000, 0), Vector3D.Forward, Vector3D.Up);
            b.DisplayName = "extract-" + variant;
            var reactor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator");
            reactor.Min = new SerializableVector3I(0, 0, 0); reactor.UseConveyorSystem = false; b.CubeBlocks.Add(reactor);
            var refinery = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Refinery>("LargeRefinery");
            refinery.Min = new SerializableVector3I(5, 0, 0); refinery.Enabled = true; refinery.UseConveyorSystem = false; b.CubeBlocks.Add(refinery);
            if (variant == "control")
            {
                var gyro = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Gyro>("LargeBlockGyro");
                gyro.Min = new SerializableVector3I(10, 0, 0); gyro.Enabled = false; b.CubeBlocks.Add(gyro);
            }
            var grid = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(b, false);
            if (variant != "zero") grid.GetFatBlocks<MyReactor>().Single().GetInventory().AddItems((MyFixedPoint)(variant == "limited" ? .001m : 10m), MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ingot>("Uranium"));
            grid.GetFatBlocks<MyRefinery>().Single().InputInventory.AddItems((MyFixedPoint)(variant == "limited" ? 10m : 10000m), MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Iron"));
        }
        return Inspect();
    }
    private void Write(string name, object value)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }
}
