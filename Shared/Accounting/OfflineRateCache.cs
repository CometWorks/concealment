// Copyright (c) 2026 CometWorks. MIT origin; see LICENSE in this directory.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Concealment.Accounting;

internal static class OfflineRateCache
{
	internal sealed record Entry(string Key, double Formula, double Rate, int Samples, double Spread, bool Scripted, int Epoch = 0);
		private static readonly object Sync = new();
	private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, Entry> Contributions = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, int> Epochs = new(StringComparer.Ordinal);
	private sealed record Snapshot(Entry[] Entries, Dictionary<string, int> Epochs);
	internal static bool TryGet(string key, out Entry entry)
	{
		lock (Sync) return Entries.TryGetValue(key, out entry) && !entry.Scripted;
	}
	internal static int Epoch(string key) { lock (Sync) return Epochs.GetValueOrDefault(key); }
	internal static void Measured(string key, double formula, double rate, int? epoch = null)
	{
		if (!double.IsFinite(rate) || rate <= 0 || !double.IsFinite(formula) || formula <= 0) return;
		lock (Sync)
		{
			if (epoch.HasValue && epoch.Value != Epochs.GetValueOrDefault(key)) return;
			double tolerance = double.Parse(HostSettings.Get("offline.cacheTolerance"), System.Globalization.CultureInfo.InvariantCulture);
			Entries.TryGetValue(key, out var previous);
			double spread = Math.Max(previous?.Spread ?? 0, previous == null ? 0 : Math.Abs(previous.Rate - rate) / Math.Max(rate, previous.Rate));
			var entry = new Entry(key, formula, rate, (previous?.Samples ?? 0) + 1, spread, spread > tolerance || Math.Abs(rate - formula) / formula > tolerance, Epochs.GetValueOrDefault(key));
			Contributions[key] = Entries[key] = entry;
		}
	}
	internal static string ContributionsRecord() { lock (Sync) return JsonSerializer.Serialize(Contributions.Values); }
	internal static void Receive(string payload) => Merge(JsonSerializer.Deserialize<Entry[]>(payload) ?? []);
	internal static string PersistedRecord() { lock (Sync) return JsonSerializer.Serialize(new Snapshot(Entries.Values.OrderBy(e => e.Key).ToArray(), new(Epochs))); }
	internal static void Restore(string payload)
	{
		if (string.IsNullOrEmpty(payload)) return;
		// The former array record remains readable; current snapshots replace the authoritative set.
		if (payload.TrimStart().StartsWith("[", StringComparison.Ordinal)) { Merge(JsonSerializer.Deserialize<Entry[]>(payload) ?? []); return; }
		var snapshot = JsonSerializer.Deserialize<Snapshot>(payload) ?? throw new InvalidOperationException("Missing offline rate cache snapshot");
		lock (Sync)
		{
			Entries.Clear(); Epochs.Clear();
			foreach (var epoch in snapshot.Epochs ?? []) Epochs[epoch.Key] = epoch.Value;
			Merge(snapshot.Entries ?? []);
			foreach (var old in Contributions.Where(c => c.Value.Epoch != Epochs.GetValueOrDefault(c.Key)).Select(c => c.Key).ToArray()) Contributions.Remove(old);
		}
	}
	private static void Merge(Entry[] entries)
	{
		lock (Sync)
			foreach (var entry in entries)
			{
				if (string.IsNullOrEmpty(entry.Key) || entry.Key.Length > 4096 || !double.IsFinite(entry.Rate) || entry.Rate <= 0 || entry.Samples <= 0
					|| entry.Epoch != Epochs.GetValueOrDefault(entry.Key)) continue;
				if (!Entries.TryGetValue(entry.Key, out var old) || entry.Samples > old.Samples || entry.Scripted && !old.Scripted) Entries[entry.Key] = entry;
				if (Contributions.TryGetValue(entry.Key, out var local) && entry.Samples >= local.Samples) Contributions.Remove(entry.Key);
			}
	}
	internal static string ClearOnAuthority(string key, int? expectedEpoch = null)
	{
		if (string.IsNullOrEmpty(key) || key.Length > 4096) throw new ArgumentException("A cache key is required");
		lock (Sync)
		{
			if (expectedEpoch.HasValue && Epochs.GetValueOrDefault(key) != expectedEpoch.Value)
				return JsonSerializer.Serialize(new { cleared = false, code = "epoch_mismatch", epoch = Epochs.GetValueOrDefault(key) });
			Epochs[key] = checked(Epochs.GetValueOrDefault(key) + 1);
			Entries.Remove(key); Contributions.Remove(key);
		}
		return JsonSerializer.Serialize(new { cleared = true, epoch = Epoch(key) });
	}

	internal static object Status() { lock (Sync) return new { count = Entries.Count, scripted = Entries.Values.Count(e => e.Scripted), entries = Entries.Values.ToArray() }; }
}
