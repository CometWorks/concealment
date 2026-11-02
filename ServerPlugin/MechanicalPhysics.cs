using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Havok;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Common.ObjectBuilders;

namespace Shared.Concealment;

// World-owned even when recovery fails before a ConcealGroup can be published.
internal sealed class MechanicalPhysics
{
    internal sealed class State
    {
        [JsonRequired] public int IntentVersion = 1;
        [JsonRequired] public bool Present;
        [JsonRequired] public bool Enabled;
        [JsonRequired] public bool ForceDisabled;
        public State Subparts;
        public bool? InWorld;
    }
    private sealed class Entry
    {
        internal MyMechanicalConnectionBlockBase Block;
        internal HkConstraint Expected;
        internal State State;
        internal bool Durable;
        internal long? Target;
        internal MyAttachableTopBlockBase Top;
        internal HkConstraint Subpart;
        internal bool SubpartInWorld;
    }
    private readonly Dictionary<long, MyCubeGrid> grids = new();
    private readonly Dictionary<long, Entry> entries = new();
    private readonly Dictionary<HkConstraint, State> owned = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HkConstraint> admitting = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HkConstraint> removed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<HkConstraint, State> releasePending = new(ReferenceEqualityComparer.Instance);
    private static readonly System.Reflection.FieldInfo EnabledField = typeof(HkConstraint).GetField("m_enabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo ForcedField = typeof(HkConstraint).GetField("m_forceDisabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    private MyMechanicalConnectionBlockBase creating;
    private bool invalid, changing, released, complete, authorized;

    private MechanicalPhysics() { released = true; }
    internal static Dictionary<long, State> SnapshotLive(MyCubeGrid grid) => grid.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>().ToDictionary(b => b.EntityId, b =>
    {
        var state = Read(b.Constraint);
        if (b is MyPistonBase piston) state.Subparts = Read(piston.m_subpartsConstraint);
        return state;
    });
    internal static MechanicalPhysics RestorePaid(MyMechanicalConnectionBlockBase block, State state)
    {
        var owner = new MechanicalPhysics();
        owner.RestoreConstraint(block.Constraint, state);
        if (block is MyPistonBase piston) owner.RestoreConstraint(piston.m_subpartsConstraint, state.Subparts);
        return owner.ReleaseComplete ? null : owner;
    }
    internal MechanicalPhysics(IEnumerable<MyCubeGrid> members)
    {
        foreach (var grid in members) grids.Add(grid.EntityId, grid);
        foreach (var block in Blocks())
        {
            var constraint = block.Constraint;
            var state = Read(constraint);
            var entry = new Entry { Block = block, Expected = constraint, State = state, Target = ((MyObjectBuilder_MechanicalConnectionBlock)block.GetObjectBuilderCubeBlock()).TopBlockId };
            entries.Add(block.EntityId, entry);
            if (block is MyPistonBase piston)
            {
                entry.Subpart = piston.m_subpartsConstraint;
                entry.SubpartInWorld = entry.Subpart?.InWorld == true;
                state.Subparts = Read(entry.Subpart);
                Own(entry.Subpart);
            }
            Own(constraint);
        }
        Hold();
    }
    internal void RestoreStates(Dictionary<long, State> saved)
    {
        if (saved == null) return;
        if (!saved.Keys.Order().SequenceEqual(entries.Keys.Order()) || saved.Values.Any(s => s == null))
            throw new InvalidDataException("Incomplete saved mechanical physics ownership");
        foreach (var entry in entries.Values)
        {
            entry.State = saved[entry.Block.EntityId];
            if (entry.State.IntentVersion != 1 || entry.State.Subparts != null && entry.State.Subparts.IntentVersion != 1)
                throw new InvalidDataException("Unknown mechanical intent representation");
            if (!entry.State.Present && (entry.State.Enabled || entry.State.ForceDisabled || entry.State.InWorld == true)
                || entry.Block is not MyPistonBase && entry.State.Subparts != null)
                throw new InvalidDataException("Invalid saved mechanical constraint absence");
            if (entry.Block is MyPistonBase && (entry.State.Subparts == null || entry.State.Subparts.InWorld == null))
                throw new InvalidDataException("Missing saved piston subpart constraint state");
            entry.Durable = true;
        }
    }
    private IEnumerable<MyMechanicalConnectionBlockBase> Blocks() => grids.Values.SelectMany(g => g.GetFatBlocks()).OfType<MyMechanicalConnectionBlockBase>();
    internal bool Contains(MyMechanicalConnectionBlockBase block) => grids.TryGetValue(block.CubeGrid.EntityId, out var grid) && ReferenceEquals(grid, block.CubeGrid);
    internal bool DeferredRelease => entries.Values.Any(e => e.Subpart != null && !e.Subpart.IsDisposed && !e.Subpart.InWorld);
    internal bool Released => released;
    internal bool ReleaseComplete
    {
        get
        {
            foreach (var constraint in releasePending.Keys.Where(c => c.IsDisposed).ToArray()) releasePending.Remove(constraint);
            return released && releasePending.Count == 0;
        }
    }
    internal bool UnpaidOwns(HkConstraint constraint) => !released && owned.ContainsKey(constraint);
    internal bool HasBlocks => entries.Count != 0;
    internal bool Intact => !released && !invalid
        && owned.Keys.All(c => c.IsDisposed || entries.Values.Any(e => ReferenceEquals(e.Expected, c) || ReferenceEquals(e.Subpart, c)))
        && Blocks().Count() == entries.Count && Blocks().All(b => entries.TryGetValue(b.EntityId, out var e)
        && ReferenceEquals(e.Block, b) && !b.MarkedForClose && ReferenceEquals(MyEntities.GetEntityById(b.EntityId), b)
        && ((MyObjectBuilder_MechanicalConnectionBlock)b.GetObjectBuilderCubeBlock()).TopBlockId == e.Target
        && (!complete || ReferenceEquals(e.Top, b.TopBlock) && (e.Top == null || ReferenceEquals(e.Top.Stator, b)))
        && ReferenceEquals(e.Expected, b.Constraint)
        && (e.Expected == null || !e.Expected.IsDisposed && e.Expected.InWorld && !e.Expected.Enabled && e.Expected.ForceDisabled)
        && (b is not MyPistonBase piston || ReferenceEquals(e.Subpart, piston.m_subpartsConstraint)
            && (e.Subpart == null || !e.Subpart.IsDisposed && e.Subpart.InWorld == e.SubpartInWorld
                && e.Subpart.ForceDisabled && (e.Subpart.InWorld ? !e.Subpart.Enabled : !(bool)EnabledField.GetValue(e.Subpart)))));

    private static State Read(HkConstraint constraint) => constraint?.IsDisposed == true ? new State { Present = true } : new State { Present = constraint != null,
        Enabled = constraint != null && (bool)EnabledField.GetValue(constraint), ForceDisabled = constraint?.ForceDisabled == true, InWorld = constraint?.InWorld == true };
    private void Subpart(Entry entry, HkConstraint constraint)
    {
        if (!ReferenceEquals(entry.Subpart, constraint))
        {
            if (ReferenceEquals(creating, entry.Block) && entry.Subpart == null && constraint != null)
            {
                entry.Subpart = constraint; entry.SubpartInWorld = constraint.InWorld;
                if (!entry.Durable) entry.State.Subparts = owned.TryGetValue(constraint, out var original) ? original : Read(constraint);
                else if (!entry.State.Subparts.Present) invalid = true;
            }
            else invalid = true;
        }
        Own(constraint);
    }
    internal void Constructed(HkConstraint constraint)
    {
        if (released || !grids.Values.Any(g => g.Physics != null && (g.Physics.RigidBody == constraint.RigidBodyA || g.Physics.RigidBody == constraint.RigidBodyB))) return;
        if (creating is MyPistonBase piston && piston.m_subpartPhysics?.RigidBody == constraint.RigidBodyB)
            Subpart(entries[creating.EntityId], constraint);
        else Own(constraint);
        if (creating == null) invalid = true;
    }
    private void Own(HkConstraint constraint)
    {
        if (constraint == null) return;
        if (!owned.ContainsKey(constraint)) owned.Add(constraint, Read(constraint));
        if (constraint.IsDisposed) { invalid = true; return; }
        if (!CanSet(constraint))
        {
            // Havok setters require world admission. Hold managed intent now;
            // Added applies the native disable before admission callbacks.
            EnabledField.SetValue(constraint, false);
            ForcedField.SetValue(constraint, true);
            return;
        }
        changing = true;
        try { constraint.ForceDisabled = true; }
        finally { changing = false; }
    }
    // Called BEFORE native assignment, including replacements that disappear again
    // before a native call throws. Never release these extra objects on rejection.
    internal void Assign(MyMechanicalConnectionBlockBase block, HkConstraint constraint)
    {
        if (released) return;
        if (!entries.TryGetValue(block.EntityId, out var entry))
        {
            invalid = true;
            entries.Add(block.EntityId, entry = new Entry { Block = block, State = new State() });
        }
        if (!ReferenceEquals(entry.Block, block)) invalid = true;
        if (!ReferenceEquals(entry.Expected, constraint))
        {
            if (ReferenceEquals(creating, block) && entry.Expected == null && constraint != null)
            {
                entry.Expected = constraint;
                if (!entry.Durable)
                {
                    var subparts = entry.State.Subparts;
                    entry.State = owned.TryGetValue(constraint, out var original) ? original : Read(constraint); entry.State.Subparts = subparts;
                }
                else if (!entry.State.Present) invalid = true;
            }
            else invalid = true;
        }
        Own(constraint);
    }
    internal void Added(HkConstraint constraint, bool allowReleasedState = true)
    {
        if (released)
        {
            if (releasePending.TryGetValue(constraint, out var state))
            {
                if (allowReleasedState)
                {
                    constraint.Enabled = state.Enabled;
                    constraint.ForceDisabled = state.ForceDisabled;
                }
                releasePending.Remove(constraint);
            }
            return;
        }
        if (!owned.ContainsKey(constraint)) return;
        removed.Remove(constraint);
        admitting.Add(constraint);
        foreach (var entry in entries.Values.Where(e => ReferenceEquals(e.Subpart, constraint)))
        {
            if (!ReferenceEquals(creating, entry.Block)) invalid = true;
            entry.SubpartInWorld = true;
        }
        Own(constraint);
    }
    internal void AdmissionFinished(HkConstraint constraint) => admitting.Remove(constraint);
    internal void Removed(HkConstraint constraint)
    {
        admitting.Remove(constraint);
        if (released || !owned.ContainsKey(constraint)) return;
        removed.Add(constraint);
        invalid = true;
        // Removal callbacks run before native membership is cleared. Revoke the
        // admission exception now, including a removal nested inside admission.
        Own(constraint);
    }
    internal bool CanSet(HkConstraint constraint) => released || !owned.ContainsKey(constraint)
        || !constraint.IsDisposed && !removed.Contains(constraint) && (constraint.InWorld || admitting.Contains(constraint));
    internal void Setting(HkConstraint constraint, bool value, bool force, ref bool allowed)
    {
        if (changing || released || !owned.ContainsKey(constraint)) return;
        if (force)
        {
            if (!value) invalid = true;
            allowed = true;
        }
        else
        {
            var entry = creating == null ? null : entries.GetValueOrDefault(creating.EntityId);
            if (entry != null && (ReferenceEquals(entry.Expected, constraint) || ReferenceEquals(entry.Subpart, constraint)))
            {
                // Legacy claims have no ephemeral-state history. Retain the actual
                // native requested state of reconstruction, never a true default.
                if (!entry.Durable)
                {
                    if (ReferenceEquals(entry.Expected, constraint)) entry.State.Enabled = value;
                    else entry.State.Subparts.Enabled = value;
                }
            }
            else if (value) invalid = true;
            allowed = false;
        }
    }
    internal void Structural(MyMechanicalConnectionBlockBase block, Action call)
    {
        if (released || creating != null) throw new InvalidOperationException("Reentrant mechanical reconstruction");
        creating = block;
        try { call(); }
        finally
        {
            // Also acquire when a native implementation bypasses its property.
            foreach (var current in Blocks())
            {
                if (current.Constraint != null && !owned.ContainsKey(current.Constraint)) Assign(current, current.Constraint);
                if (current is MyPistonBase piston) Subpart(entries[current.EntityId], piston.m_subpartsConstraint);
            }
            creating = null;
            Hold();
        }
    }
    internal void Hold()
    {
        foreach (var block in Blocks())
            if (!entries.TryGetValue(block.EntityId, out var entry) || !ReferenceEquals(entry.Block, block) || !ReferenceEquals(entry.Expected, block.Constraint))
            { invalid = true; Own(block.Constraint); }
        foreach (var entry in entries.Values)
            if (entry.Block is MyPistonBase piston)
            {
                if (!ReferenceEquals(entry.Subpart, piston.m_subpartsConstraint)) invalid = true;
                Own(piston.m_subpartsConstraint);
            }
        foreach (var constraint in owned.Keys)
            if (!constraint.IsDisposed) Own(constraint);
        if (!HasBlocks) return;
        foreach (var grid in grids.Values)
        {
            grid.Physics?.RigidBody?.Deactivate();
            grid.Physics?.RigidBody2?.Deactivate();
        }
    }
    internal Dictionary<long, State> Snapshot() => entries.ToDictionary(p => p.Key, p => p.Value.State);
    internal void ValidateComplete()
    {
        if (!Intact || entries.Values.Any(e => e.State.Present != (e.Expected != null)
            || e.Block is MyPistonBase && (e.State.Subparts.Present != (e.Subpart != null))))
            throw new InvalidDataException("Saved mechanical constraint presence changed during recovery");
        foreach (var entry in entries.Values) entry.Top = entry.Block.TopBlock;
        complete = true;
    }
    internal void AuthorizeRelease() => authorized = true;
    internal void Release()
    {
        if (!authorized) throw new InvalidOperationException("Mechanical physics still belongs to an unpaid interval");
        if (!released && !Intact || entries.Values.Any(e => !ReferenceEquals(e.Expected, e.Block.Constraint)
            || e.Expected?.IsDisposed == true || e.Subpart?.IsDisposed == true
            || e.Block is MyPistonBase piston && !ReferenceEquals(e.Subpart, piston.m_subpartsConstraint)))
            throw new InvalidDataException("Held mechanical constraint identity/state changed before reveal");
        released = true;
        foreach (var entry in entries.Values)
        {
            RestoreConstraint(entry.Expected, entry.State);
            if (entry.Block is MyPistonBase) RestoreConstraint(entry.Subpart, entry.State.Subparts);
        }
        owned.Clear();
        admitting.Clear();
        removed.Clear();
    }
    private void RestoreConstraint(HkConstraint constraint, State state)
    {
        if (constraint == null) return;
        if (constraint.InWorld)
        {
            constraint.Enabled = state.Enabled;
            constraint.ForceDisabled = state.ForceDisabled;
        }
        else
        {
            // Native setters require world admission. Restore the managed
            // intent of a deliberately deferred, inactive subpart instead.
            releasePending[constraint] = state;
            EnabledField.SetValue(constraint, state.Enabled);
            ForcedField.SetValue(constraint, state.ForceDisabled);
        }
    }

}
