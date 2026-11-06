// Copyright (c) 2026 CometWorks. Extracted from Cluster (MIT origin).
// See ServerPlugin/Accounting/LICENSE and NOTICE for the retained permission notice.
using System.Collections.Generic;
using System.Linq;

namespace Concealment.Accounting;

/// <summary>SE-1096: what one OfflineModel.Simulate call looks up again and again, built once. The stock sums and recipe checks
/// are memos of the same expressions over the same stacks, dropped by every stock change, so every value and its
/// decimal scale are the ones a fresh evaluation gives. The allocation plan is the LINQ grouping and ordering of
/// AllocatePass over the profile's fixed fields (resource, priority, group, pair, mode).</summary>
internal sealed class ModelIndex
{
	private long version;
	private readonly long[] changed;
	private readonly Dictionary<(OfflineRecipe, decimal), (long At, bool Value)> feasible = new();
	private readonly Dictionary<OfflineRecipe, int[]> touches = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<OfflineMaterial, (long At, decimal Value)> available = new(ReferenceEqualityComparer.Instance);
	internal readonly int[][] SourcesOf;
	internal readonly (int Sink, int[] Sources)[] FeedbackPorts;
	internal readonly ResourcePlan[] Resources;
	internal readonly bool[] ElectricSource, ElectricSink, ElectricStorage;

	internal ModelIndex(OfflineProfile p)
	{
		changed = new long[p.Inventories.Count];
		for (int i = 0; i < p.Inventories.Count; i++) p.Inventories[i].Memo = new(this, i);
		SourcesOf = SourcesOfStorage(p);
		FeedbackPorts = p.Sources.Select((s, i) => (Source: s, Index: i)).Where(s => s.Source.PowerFeedback).GroupBy(s => s.Source.PowerSink)
			.Select(g => (g.Key, g.Select(s => s.Index).ToArray())).ToArray();
		Resources = ResourcePlans(p);
		ElectricSource = p.Sources.Select(s => s.Resource == OfflineModel.Electricity).ToArray();
		ElectricSink = p.Sinks.Select(s => s.Resource == OfflineModel.Electricity).ToArray();
		ElectricStorage = p.Storage.Select(s => s.Resource == OfflineModel.Electricity).ToArray();
	}

	// Allocate is also called on a bare profile, outside Simulate.
	internal static int[][] SourcesOfStorage(OfflineProfile p) =>
		Enumerable.Range(0, p.Storage.Count).Select(s => Enumerable.Range(0, p.Sources.Count).Where(i => p.Sources[i].Storage == s).ToArray()).ToArray();
	internal static ResourcePlan[] ResourcePlans(OfflineProfile p) =>
		p.Sinks.Select(s => s.Resource).Concat(p.Sources.Select(s => s.Resource)).Concat(p.Storage.Select(s => s.Resource)).Distinct()
			.Select(resource => new ResourcePlan(p, resource)).ToArray();

	internal void Changed(int inventory) => changed[inventory] = ++version;

	// A recipe check reads only the stock and free room of the inventories its legs name.
	internal bool CanMake(OfflineProfile p, OfflineRecipe recipe, decimal units)
	{
		if (!touches.TryGetValue(recipe, out var used))
			touches[recipe] = used = recipe.Inputs.Concat(recipe.Outputs).SelectMany(m => m.Inventories).Distinct().ToArray();
		if (feasible.TryGetValue((recipe, units), out var memo) && Fresh(used, memo.At)) return memo.Value;
		bool value = OfflineModel.RecipeTransaction(p, recipe, units, false);
		feasible[(recipe, units)] = (version, value);
		return value;
	}

	internal decimal Available(OfflineProfile p, OfflineMaterial material)
	{
		if (available.TryGetValue(material, out var memo) && Fresh(material.Inventories, memo.At)) return memo.Value;
		decimal value = material.Inventories.Sum(i => OfflineModel.Amount(p.Inventories[i], material.Item));
		available[material] = (version, value);
		return value;
	}

	private bool Fresh(int[] inventories, long at)
	{
		foreach (int i in inventories) if (changed[i] > at) return false;
		return true;
	}

	internal static void Release(OfflineProfile p)
	{
		p.Index = null;
		foreach (var inventory in p.Inventories) inventory.Memo = null;
	}
}

internal sealed class InventoryMemo(ModelIndex index, int inventory)
{
	internal decimal? Volume, Mass;
	internal readonly Dictionary<string, decimal> Amounts = new();

	internal void Changed()
	{
		Volume = Mass = null;
		Amounts.Clear();
		index.Changed(inventory);
	}
}

/// <summary>AllocatePass for one resource. Groups keep LINQ's order: GroupBy by first appearance, then a stable
/// OrderBy priority. Sinks come before non-paired storage in the ordinary groups, as they were concatenated.</summary>
internal sealed class ResourcePlan
{
	internal readonly int[] Sources, Storage, Stockpiles, Autos, External;
	internal readonly (int Index, bool Store, bool Adaptible, int Slot)[][] Ordinary;
	internal readonly int[][] Dispatch;

	internal ResourcePlan(OfflineProfile p, string resource)
	{
		Sources = Enumerable.Range(0, p.Sources.Count).Where(i => p.Sources[i].Resource == resource).ToArray();
		Storage = Enumerable.Range(0, p.Storage.Count).Where(i => p.Storage[i].Resource == resource).ToArray();
		var ordinary = Enumerable.Range(0, p.Sinks.Count).Where(i => p.Sinks[i].Resource == resource)
			.Select(i => (Key: (p.Sinks[i].Priority, p.Sinks[i].Group), Entry: (Index: i, Store: false, p.Sinks[i].Adaptible, Slot: -1)))
			.Concat(Storage.Select((s, slot) => (Index: s, Slot: slot)).Where(s => !p.Storage[s.Index].Pair)
				.Select(s => (Key: (p.Storage[s.Index].Priority, p.Storage[s.Index].Group), Entry: (s.Index, Store: true, Adaptible: true, s.Slot))))
			.ToList();
		Ordinary = ordinary.GroupBy(s => s.Key).OrderBy(g => g.Key.Priority).Select(g => g.Select(s => s.Entry).ToArray()).ToArray();
		Dispatch = Sources.GroupBy(i => p.Sources[i].Storage >= 0 && p.Storage[p.Sources[i].Storage].Pair
			? (Priority: int.MinValue, Group: "") : (p.Sources[i].Priority, p.Sources[i].Group)).OrderBy(g => g.Key.Priority).Select(g => g.ToArray()).ToArray();
		Stockpiles = Enumerable.Range(0, Storage.Length).Where(k => p.Storage[Storage[k]].Pair && p.Storage[Storage[k]].Mode == 1).ToArray();
		Autos = Enumerable.Range(0, Storage.Length).Where(k => p.Storage[Storage[k]].Pair && p.Storage[Storage[k]].Mode != 1).ToArray();
		External = Sources.Where(i => p.Sources[i].Storage < 0 || !p.Storage[p.Sources[i].Storage].Pair).ToArray();
	}
}
