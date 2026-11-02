using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ExtractionProbe;

public sealed partial class Probe
{
    private static string mechanicalRoot;
    private static int structuralCalls, attachmentCalls, mechanicalUpdates;
    private void InitializeMechanicalTrace()
    {
        mechanicalRoot = root;
        var harmony = new Harmony("ExtractionProbe.mechanical-recovery");
        harmony.Patch(AccessTools.Method(PluginType.Assembly.GetType("Shared.Concealment.StandaloneAccounting"), "RestoreMechanicalLinks"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(BeforeStructural)), postfix: new HarmonyMethod(typeof(Probe), nameof(AfterStructural)));
        harmony.Patch(AccessTools.Method(typeof(MyMechanicalConnectionBlockBase), "UpdateAttachState"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(BeforeAttachment)), postfix: new HarmonyMethod(typeof(Probe), nameof(AfterAttachment)));
        harmony.Patch(AccessTools.Method(typeof(MyMechanicalConnectionBlockBase), "UpdateOnceBeforeFrame"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(CountMechanicalUpdate)));
    }
    private static void BeforeStructural() { structuralCalls++; TraceMechanical("structural-before-" + structuralCalls); }
    private static void AfterStructural() => TraceMechanical("structural-after-" + structuralCalls);
    private static void BeforeAttachment(MyMechanicalConnectionBlockBase __instance) { attachmentCalls++; TraceMechanical("attach-before-" + attachmentCalls, __instance.EntityId); }
    private static void AfterAttachment(MyMechanicalConnectionBlockBase __instance) => TraceMechanical("attach-after-" + attachmentCalls, __instance.EntityId);
    private static void CountMechanicalUpdate() => mechanicalUpdates++;
    private static void TraceMechanical(string label, long block = 0)
    {
        if (MySession.Static == null) return;
        File.WriteAllText(Path.Combine(mechanicalRoot, label + ".json"), JsonSerializer.Serialize(new {
            block, structuralCalls, attachmentCalls, mechanicalUpdates,
            onGameThread = Thread.CurrentThread == MySandboxGame.Static.UpdateThread,
            state = Inspect(), mechanical = MechanicalState()
        }, Json));
    }
    private static object MechanicalState() => MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName.StartsWith("extract-"))
        .SelectMany(g => g.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>()).Select(b => new {
            block = b.EntityId, grid = b.CubeGrid.EntityId, type = b.GetType().FullName, b.IsAttached,
            target = ((MyObjectBuilder_MechanicalConnectionBlock)b.GetObjectBuilderCubeBlock()).TopBlockId,
            top = b.TopBlock?.EntityId, topGrid = b.TopGrid?.EntityId, parent = b.TopBlock?.Stator?.EntityId,
            flags = (int)b.Flags, needsUpdate = b.NeedsUpdate.ToString(), b.UpdateAttach, b.NeedReattach,
            subpartConstraintEnabled = b is MyPistonBase pc ? pc.m_subpartsConstraint?.Enabled : null,
            subpartConstraintForced = b is MyPistonBase pf ? pf.m_subpartsConstraint?.ForceDisabled : null,
            subpartConstraintInWorld = b is MyPistonBase pw ? pw.m_subpartsConstraint?.InWorld : null,
            subpartConstraintIdentity = b is MyPistonBase pi && pi.m_subpartsConstraint != null ? (int?)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pi.m_subpartsConstraint) : null,
            subpartsReady = b is MyPistonBase piston ? (bool?)(piston.Subpart3 != null) : null,
            constraintEnabled = b.Constraint?.Enabled, constraintForced = b.Constraint?.ForceDisabled,
            constraintIdentity = b.Constraint == null ? (int?)null : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.Constraint),
            constraintDisposed = b.Constraint?.IsDisposed,
            physics = b.CubeGrid.Physics?.Enabled,
        }).ToArray();
    private static object PistonFixtures()
    {
        Fixtures();
        var grid = Named("extract-powered");
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_PistonBase>("LargePistonBase");
        builder.Min = new SerializableVector3I(10, 0, 0); builder.Velocity = 0;
        grid.AddBlock(builder, false);
        return Inspect();
    }
    private static object PistonTop()
    {
        var piston = Named("extract-powered").GetFatBlocks<MyPistonBase>().Single();
        piston.RecreateTop(piston.OwnerId, instantBuild: true);
        if (piston.TopGrid == null) throw new InvalidOperationException("Native piston top missing");
        piston.TopGrid.DisplayName = "extract-recovery-top";
        return Inspect();
    }
}
