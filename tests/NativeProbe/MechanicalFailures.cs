using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using Havok;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRage;
using VRage.ObjectBuilders.Private;

namespace ExtractionProbe;

public sealed partial class Probe
{
    private static string recoveryFault;
    private static int faultCallbacks;
    private static MyMechanicalConnectionBlockBase firstLink;
    private static bool transactionFaultFired, mixedCapture;
    private static readonly HashSet<long> subscribed = new();
    private void InitializeMechanicalFailures()
    {
        recoveryFault = System.Environment.GetEnvironmentVariable("EXTRACTION_RECOVERY_FAULT");
        var harmony = new Harmony("ExtractionProbe.mechanical-failures");
        harmony.Patch(AccessTools.Method(PluginType.Assembly.GetType("Shared.Concealment.StandaloneAccounting"), "RestoreMechanicalLinks"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(SubscribeMechanicalFailure)));
        harmony.Patch(AccessTools.Method(PluginType.Assembly.GetType("Shared.Concealment.StandaloneAccounting"), "Frozen"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(MixedBeforeHold)));
    }
    private static void SubscribeMechanicalFailure()
    {
        if (string.IsNullOrEmpty(recoveryFault)) return;
        foreach (var block in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName.StartsWith("extract-"))
            .SelectMany(g => g.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>()))
            if (subscribed.Add(block.EntityId)) typeof(MyMechanicalConnectionBlockBase).GetEvent("OnAttachmentUpdate").AddEventHandler(block, (Action<Sandbox.ModAPI.IMyMechanicalConnectionBlock>)MechanicalFailure);
    }
    private static void MechanicalFailure(Sandbox.ModAPI.IMyMechanicalConnectionBlock connection)
    {
        var block = (MyMechanicalConnectionBlockBase)connection;
        if (block.TopBlock == null) return;
        faultCallbacks++;
        firstLink ??= block;
        FaultTrace("callback-before-" + faultCallbacks);
        if (recoveryFault == "throw-first" || recoveryFault == "throw-later" && faultCallbacks == 2)
            throw new InvalidOperationException("Documented real OnAttachmentUpdate fixture exception AFTER native creation");
        if (recoveryFault == "stock-first" && faultCallbacks == 1)
        {
            // Deliberate external fixture mutation, never an accounting debit.
            Named("extract-powered").GetFatBlocks<MyRefinery>().Single().InputInventory.AddItems(1,
                MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Iron"));
        }
        if (recoveryFault == "detach-earlier" && faultCallbacks == 2)
            ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)firstLink).Detach();
        FaultTrace("callback-after-" + faultCallbacks);
    }
    private static void FaultTrace(string name) => File.WriteAllText(Path.Combine(mechanicalRoot, "fault-" + name + ".json"),
        JsonSerializer.Serialize(new { recoveryFault, faultCallbacks, fixtureMutation = recoveryFault == "stock-first", state = Inspect() }, Json));
    private static object TwoLinkFixtures()
    {
        RecoveryFixtures();
        var grid = Named("extract-powered");
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_MotorStator>("LargeStator");
        builder.Min = new SerializableVector3I(30, 0, 0);
        var rotor = (MyMotorStator)grid.AddBlock(builder, false).FatBlock;
        rotor.RecreateTop(builder.Owner, instantBuild: true);
        rotor.TargetVelocityRPM = 0;
        ((Sandbox.ModAPI.Ingame.IMyMotorStator)rotor).RotorLock = true;
        rotor.TopGrid.DisplayName = "extract-recovery-top-2";
        var absent = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_MotorStator>("LargeStator");
        absent.Min = new SerializableVector3I(50, 0, 0);
        var empty = (MyMotorStator)grid.AddBlock(absent, false).FatBlock;
        empty.TargetVelocityRPM = 0;
        ((Sandbox.ModAPI.Ingame.IMyMotorStator)empty).RotorLock = true;
        return Inspect();
    }
    private static object MixedConceal()
    {
        mixedCapture = true;
        var before = Inspect();
        Manager.GetType().GetMethod("ConcealGrids").Invoke(Manager, new object[] { 15000.0 });
        return new { before, after = Inspect(), readiness = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName.StartsWith("extract-"))
            .Select(g => new { grid = g.EntityId, reason = Engine.GetMethod("Readiness", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { g }),
                velocity = g.Physics?.LinearVelocity.ToString(), acceleration = g.Physics?.LinearAcceleration.ToString() }).ToArray() };
    }
    private static void MixedBeforeHold(object __0)
    {
        if (!mixedCapture) return;
        var grids = (IEnumerable<MyCubeGrid>)__0.GetType().GetProperty("Grids").GetValue(__0);
        var bases = grids.SelectMany(g => g.GetFatBlocks<MyMechanicalConnectionBlockBase>()).Where(b => b.Constraint != null).OrderBy(b => b.EntityId).ToArray();
        if (bases.Length == 1 && bases[0] is MyPistonBase piston)
        {
            // Deliberate fixture flag mutation. Native Havok setters require
            // world admission; inactive subparts only carry managed intent.
            var constraint = piston.m_subpartsConstraint;
            if (constraint.InWorld) constraint.Enabled = false;
            else AccessTools.Field(typeof(HkConstraint), "m_enabled").SetValue(constraint, false);
        }
        else if (bases.Length == 2) bases[1].Constraint.Enabled = false;
        else return;
        mixedCapture = false;
        FaultTrace("mixed-original-before-hold");
    }
    private static object ArmTransactionFault(string mode)
    {
        transactionFaultFired = false;
        var inventory = Named("extract-powered").GetFatBlocks<MyRefinery>().Single().InputInventory;
        Action<VRage.Game.Entity.MyInventoryBase> callback = _ =>
        {
            if (transactionFaultFired) return;
            transactionFaultFired = true;
            var block = Named("extract-powered").GetFatBlocks<MyMechanicalConnectionBlockBase>().First(b => b.Constraint != null);
            FaultTrace("transaction-before-" + mode);
            if (mode == "stock")
            {
                try { inventory.AddItems(1, MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Ore>("Iron")); }
                catch (Exception)
                {
                    FaultTrace("transaction-refused-before-stock-write");
                    throw;
                }
                throw new InvalidOperationException("External native stock mutation was not refused");
            }
            ChangeMechanical(block, mode);
            FaultTrace("transaction-after-" + mode);
        };
        typeof(VRage.Game.Entity.MyInventoryBase).GetEvent("ContentsChanged").AddEventHandler(inventory, callback);
        return Inspect();
    }
    private static void ChangeMechanical(MyMechanicalConnectionBlockBase block, string mode)
    {
        if (mode == "subpart-enable") ((MyPistonBase)block).m_subpartsConstraint.Enabled = true;
        else if (mode == "subpart-replace")
        {
            var piston = (MyPistonBase)block;
            bool active = piston.m_subpartsConstraint.InWorld;
            var replacement = new HkConstraint(block.CubeGrid.Physics.RigidBody, piston.m_subpartPhysics.RigidBody, new HkFixedConstraintData());
            piston.m_subpartsConstraint = replacement;
            if (active) block.CubeGrid.Physics.AddConstraint(replacement);
            replacement.Enabled = true;
        }
        else if (mode == "enable") block.Constraint.Enabled = true;
        else if (mode == "unforce") block.Constraint.ForceDisabled = false;
        else if (mode == "detach") ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)block).Detach();
        else if (mode == "replace" || mode == "dispose" || mode == "add")
        {
            var original = block.Constraint;
            var replacement = new HkConstraint(original.RigidBodyA, original.RigidBodyB, new HkFixedConstraintData());
            var target = mode == "add" ? block.CubeGrid.GetFatBlocks<MyMechanicalConnectionBlockBase>().Single(b => b.Constraint == null) : block;
            target.Constraint = replacement;
            block.CubeGrid.Physics.AddConstraint(replacement);
            replacement.Enabled = true;
            if (mode == "dispose") { block.CubeGrid.Physics.RemoveConstraint(original); original.Dispose(); }
        }
        else throw new ArgumentException("Unknown mechanical fault");
    }
}
