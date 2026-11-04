// Copyright (c) 2026 CometWorks. Extracted from Cluster (MIT origin).
// See ServerPlugin/Accounting/LICENSE and NOTICE for the retained permission notice.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Concealment.Accounting;

internal static partial class OfflineModel
{
	/// <summary>Skip only exact, observed storage-state repetitions. Brent's single
	/// checkpoint bounds memory; changing stocks/queues always use ordinary events.
	/// No fuel settlement, solar transition or zone renewal is extrapolated.</summary>
	private sealed class StorageCycles(OfflineProfile initial)
	{
		private readonly int anchor = initial.Sources.Any(s => s.Fuel.Count > 0 && s.FuelTickFrames == 0) ? -1
			: initial.Storage.FindIndex(s => s.Enabled && !s.Creative && s.TickFrames > 0);
		private CycleState previous;
		private int distance, window = 1;
		internal void Reset() { previous = null; distance = 0; window = 1; }

		internal double Advance(OfflineProfile p, OfflineResult result, double elapsed, double boundary)
		{
			if (anchor < 0 || p.Storage[anchor].ElapsedFrames != 0) return 0;
			// Integer event steps/counters below 2^52 make repeated timer additions
			// exact. Fractional/native-feedback prefixes retain ordinary evaluation.
			double[] timers = Timers(p);
			if (!Whole(elapsed) || !Whole(boundary) || timers.Any(t => !Whole(t))) { Reset(); return 0; }
			if (previous != null)
			{
				distance++;
				if (previous.Matches(p))
				{
					double period = elapsed - previous.Elapsed;
					double count = Math.Floor((boundary - elapsed - 1) / period);
					for (int i = 0; i < timers.Length; i++)
					{
						double delta = timers[i] - previous.Timers[i];
						// Fuel elapsed increases; all other counters decrease or pause.
						bool fuelElapsed = i < p.Sources.Count * 2 && i % 2 == 0;
						if (fuelElapsed ? delta < 0 : delta > 0) { Reset(); return 0; }
						if (delta == 0) continue;
						double remaining = fuelElapsed ? p.Sources[i / 2].FuelTickFrames - timers[i] : timers[i];
						count = Math.Min(count, Math.Floor((remaining - 1) / Math.Abs(delta)));
					}
					if (count > 0)
					{
						for (int i = 0; i < p.Sources.Count; i++)
						{
							p.Sources[i].FuelElapsedFrames += count * (timers[2 * i] - previous.Timers[2 * i]);
							p.Sources[i].FuelTier2AfterFrames += count * (timers[2 * i + 1] - previous.Timers[2 * i + 1]);
						}
						for (int i = 0; i < p.Upkeep.Count; i++)
						{
							int j = 2 * (p.Sources.Count + i);
							p.Upkeep[i].ActivationRemainingFrames += count * (timers[j] - previous.Timers[j]);
							p.Upkeep[i].RemainingFrames += count * (timers[j + 1] - previous.Timers[j + 1]);
						}
						var metrics = Metrics(result);
						for (int i = 0; i < metrics.Length; i++)
						foreach (var pair in metrics[i].ToArray())
							metrics[i][pair.Key] += count * (pair.Value - CollectionExtensions.GetValueOrDefault(previous.Metrics[i], pair.Key));
						Reset();
						return count * period;
					}
				}
				if (distance < window) return 0;
				window = Math.Min(1_048_576, window * 2);
			}
			previous = new CycleState(p, result, elapsed, timers);
			distance = 0;
			return 0;
		}

		private static bool Whole(double value) => double.IsFinite(value) && Math.Abs(value) <= 4503599627370496d && value == Math.Truncate(value);
		private static double[] Timers(OfflineProfile p) => p.Sources.SelectMany(s => new[] { s.FuelElapsedFrames, s.FuelTier2AfterFrames })
			.Concat(p.Upkeep.SelectMany(z => new[] { z.ActivationRemainingFrames, z.RemainingFrames })).ToArray();
		private static Dictionary<long, double>[] Metrics(OfflineResult r) => [r.ProducerEnergyMWh, r.ConsumerEnergyMWh,
			r.StorageInputEnergyMWh, r.SolarShadowAllowanceMWh, r.ProductionUnits];

		private sealed class CycleState(OfflineProfile p, OfflineResult result, double elapsed, double[] timers)
		{
			internal readonly double Elapsed = elapsed;
			internal readonly double[] Timers = timers;
			internal readonly Dictionary<long, double>[] Metrics = StorageCycles.Metrics(result).Select(m => new Dictionary<long, double>(m)).ToArray();
			private readonly (int, string, decimal)[] stocks = Stocks(p).ToArray();
			private readonly (double, double, double, bool, bool, bool)[] stores = Stores(p).ToArray();
			private readonly (int, string, decimal, uint)[] queues = Queues(p).ToArray();
			private readonly double[] progress = p.Production.Select(b => b.Progress).ToArray();
			private readonly bool[] powered = p.Sinks.Select(s => s.Powered).ToArray(), producing = p.Sources.Select(s => s.Producing).ToArray();
			private readonly (bool, bool)[] zones = p.Upkeep.Select(z => (z.Enabled, z.Exists)).ToArray();
			private readonly double resourceFrame = p.ResourceFrameRemaining;
			internal bool Matches(OfflineProfile value) => resourceFrame == value.ResourceFrameRemaining
				&& stocks.SequenceEqual(Stocks(value)) && stores.SequenceEqual(Stores(value)) && queues.SequenceEqual(Queues(value))
				&& progress.SequenceEqual(value.Production.Select(b => b.Progress)) && powered.SequenceEqual(value.Sinks.Select(s => s.Powered))
				&& producing.SequenceEqual(value.Sources.Select(s => s.Producing)) && zones.SequenceEqual(value.Upkeep.Select(z => (z.Enabled, z.Exists)));
			private static IEnumerable<(int, string, decimal)> Stocks(OfflineProfile p) => p.Inventories.SelectMany((inv, i) => inv.Stacks.Select(s => (i, s.Item, s.Amount)));
			private static IEnumerable<(double, double, double, bool, bool, bool)> Stores(OfflineProfile p) => p.Storage.Select(s => (s.Amount, s.ElapsedFrames, s.AccountedEnergy, s.Full, s.Sampling, s.Transferring));
			private static IEnumerable<(int, string, decimal, uint)> Queues(OfflineProfile p) => p.Production.SelectMany((b, i) => b.Queue.Select(q => (i, q.Recipe, q.Amount, q.ItemId)));
		}
	}
}
