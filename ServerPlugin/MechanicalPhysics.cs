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
        [JsonRequired] public bool Present;
        [JsonRequired] public bool Enabled;
        [JsonRequired] public bool ForceDisabled;
    }
    private sealed class Entry
    {
        internal MyMechanicalConnectionBlockBase Block;
        internal HkConstraint Expected;
        internal State State;
        internal bool Durable;
        internal long? Target;
        internal MyAttachableTopBlockBase Top;
    }
    private readonly Dictionary<long, MyCubeGrid> grids = new();
    private readonly Dictionary<long, Entry> entries = new();
    private readonly HashSet<HkConstraint> owned = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HkConstraint> added = new(ReferenceEqualityComparer.Instance);
    private MyMechanicalConnectionBlockBase creating;
    private bool invalid, changing, released, complete;

    internal MechanicalPhysics(IEnumerable<MyCubeGrid> members)
    {
        foreach (var grid in members) grids.Add(grid.EntityId, grid);
        foreach (var block in Blocks())
        {
            var constraint = block.Constraint;
            var state = new State { Present = constraint != null,
                Enabled = constraint?.Enabled == true, ForceDisabled = constraint?.ForceDisabled == true };
            entries.Add(block.EntityId, new Entry { Block = block, Expected = constraint, State = state, Target = ((MyObjectBuilder_MechanicalConnectionBlock)block.GetObjectBuilderCubeBlock()).TopBlockId });
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
            entry.Durable = true;
        }
    }
    private IEnumerable<MyMechanicalConnectionBlockBase> Blocks() => grids.Values.SelectMany(g => g.GetFatBlocks()).OfType<MyMechanicalConnectionBlockBase>();
    internal bool Contains(MyMechanicalConnectionBlockBase block) => grids.TryGetValue(block.CubeGrid.EntityId, out var grid) && ReferenceEquals(grid, block.CubeGrid);
    internal bool HasBlocks => entries.Count != 0;
    internal bool Intact => !released && !invalid && Blocks().Count() == entries.Count && Blocks().All(b => entries.TryGetValue(b.EntityId, out var e)
        && ReferenceEquals(e.Block, b) && !b.MarkedForClose && ReferenceEquals(MyEntities.GetEntityById(b.EntityId), b)
        && ((MyObjectBuilder_MechanicalConnectionBlock)b.GetObjectBuilderCubeBlock()).TopBlockId == e.Target
        && (!complete || ReferenceEquals(e.Top, b.TopBlock) && (e.Top == null || ReferenceEquals(e.Top.Stator, b)))
        && ReferenceEquals(e.Expected, b.Constraint)
        && (e.Expected == null || !e.Expected.IsDisposed && e.Expected.InWorld && !e.Expected.Enabled && e.Expected.ForceDisabled));

    private void Own(HkConstraint constraint)
    {
        if (constraint == null) return;
        owned.Add(constraint);
        if (constraint.IsDisposed) { invalid = true; return; }
        if (!constraint.InWorld && !added.Contains(constraint)) return;
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
                if (!entry.Durable) entry.State = new State { Present = true, Enabled = constraint.Enabled, ForceDisabled = constraint.ForceDisabled };
                else if (!entry.State.Present) invalid = true;
            }
            else invalid = true;
        }
        Own(constraint);
    }
    internal void Added(HkConstraint constraint)
    {
        if (released || !owned.Contains(constraint)) return;
        added.Add(constraint);
        Own(constraint);
    }
    internal bool CanSet(HkConstraint constraint) => released || !owned.Contains(constraint) || !constraint.IsDisposed && (constraint.InWorld || added.Contains(constraint));
    internal void Setting(HkConstraint constraint, bool value, bool force, ref bool allowed)
    {
        if (changing || released || !owned.Contains(constraint)) return;
        if (force)
        {
            if (!value) invalid = true;
            allowed = true;
        }
        else
        {
            var entry = creating == null ? null : entries.GetValueOrDefault(creating.EntityId);
            if (entry != null && ReferenceEquals(entry.Expected, constraint))
            {
                // Legacy claims have no ephemeral-state history. Retain the actual
                // native requested state of reconstruction, never a true default.
                if (!entry.Durable) entry.State.Enabled = value;
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
                if (current.Constraint != null && !owned.Contains(current.Constraint)) Assign(current, current.Constraint);
            creating = null;
            Hold();
        }
    }
    internal void Hold()
    {
        foreach (var block in Blocks())
            if (!entries.TryGetValue(block.EntityId, out var entry) || !ReferenceEquals(entry.Block, block) || !ReferenceEquals(entry.Expected, block.Constraint))
            { invalid = true; Own(block.Constraint); }
        foreach (var constraint in owned)
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
        if (!Intact || entries.Values.Any(e => e.State.Present != (e.Expected != null)))
            throw new InvalidDataException("Saved mechanical constraint presence changed during recovery");
        foreach (var entry in entries.Values) entry.Top = entry.Block.TopBlock;
        complete = true;
    }
    internal void Release()
    {
        if (!Intact) throw new InvalidDataException("Held mechanical constraint identity/state changed before reveal");
        released = true;
        foreach (var entry in entries.Values)
            if (entry.Expected != null)
            {
                entry.Expected.Enabled = entry.State.Enabled;
                entry.Expected.ForceDisabled = entry.State.ForceDisabled;
            }
        owned.Clear();
        added.Clear();
    }
}
