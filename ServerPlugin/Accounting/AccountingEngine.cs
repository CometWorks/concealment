// Copyright (c) 2026 CometWorks. Extracted from Cluster (MIT origin).
// See ServerPlugin/Accounting/LICENSE and NOTICE for the retained permission notice.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.Game.ObjectBuilders.Components.Beacon;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;

namespace Concealment.Accounting;

internal static partial class AccountingEngine
{
	private static readonly Dictionary<long, OfflineResult> Results = new();
	internal static long Frame => Host.NativeFrame != null ? Host.NativeFrame() : checked((long)Math.Floor(Host.ClockMilliseconds() / 16));
	internal const long FramesPerAuthorityHour = 3600000 / 16;
	// Native world time advances 16ms per completed update. Resource rates use
	// 1/60s per update; Cluster settles separately in durable WA work addresses.
	internal static long MaxFrames => checked(HostSettings.GetInt("offline.compensateMaxHours") * FramesPerAuthorityHour);
	internal static int MaxEvents => HostSettings.GetInt("offline.compensateMaxEvents");

	/// <summary>Runs once, at the shared recovery seam, before any entity is instantiated. The source
	/// save is immutable: a failed wake re-reads it; an ordinary new-generation Final drops profiles.</summary>
	internal static void Apply(IDictionary<long, MyObjectBuilder_EntityBase> entities,
		IEnumerable<(OfflineProfile Profile, HashSet<long> Members)> profiles)
	{
		if (!HostSettings.GetBool("offline.compensate")) return;
		var entries = profiles.ToArray();
		if (entries.Length == 0) return;
		var wake = BeginWake(entries.Select(e => e.Profile).ToArray(), null, null);
		wake.Run();
		Apply(entities, wake, entries.Select(e => e.Members).ToArray());
	}

	/// <summary>SE-1096: the models of one wake. A group offline for 14 hours needed 150,000 model events, and on the
	/// game thread that held a node for four minutes until the registry fenced it. So the game thread only validates the
	/// timebase and takes the session's inputs here; <see cref="Run"/> hashes the definitions, reads the sun/work paths and runs the models on a
	/// worker thread; the host admits and commits on the game thread once <see cref="Ready"/>. The models see the same
	/// inputs as a synchronous wake at the same frame, so their results are the same.</summary>
	internal sealed class Wake
	{
		internal byte[][] Sections;
		internal int[] Section;
		internal OfflineProfile[] Profiles;
		internal (string Epoch, long Frame, long Generation, long SunTicks) Authority;
		internal long Frame;
		internal bool[] Content;
		internal Exception[] ContentFailures;
		internal bool Rotating;
		internal double Period;
		internal long MaxFrames;
		internal int MaxEvents;
		// The first failed path in profile order: no group of this wake may commit.
		internal Exception PathFailure;
		internal (OfflineProfile Model, OfflineResult Result)[] Models;
		internal Exception[] Failures;
		internal double[] Seconds;
		private volatile bool m_ready;
		internal bool Ready => m_ready;

		internal void Run()
		{
			try
			{
				var paths = new SunWorkPath[Profiles.Length];
				for (int i = 0; i < Profiles.Length; i++)
				{
					try { paths[i] = ReadSunPath(Profiles[i], Frame, Rotating, MaxFrames); }
					catch (Exception e) { PathFailure = e; return; }
				}
				Models = new (OfflineProfile, OfflineResult)[Profiles.Length];
				Failures = new Exception[Profiles.Length];
				Seconds = new double[Profiles.Length];
				Content = new bool[Profiles.Length];
				ContentFailures = new Exception[Profiles.Length];
				for (int i = 0; i < Profiles.Length; i++)
				{
					// Fingerprinting the definitions took up to 2 s per wake on the game thread. It only reads
					// definitions and session settings, which do not change while the session runs.
					try { Content[i] = Profiles[i].ContentHash == ContentHash(Profiles[i].Definitions); }
					catch (Exception e) { ContentFailures[i] = e; continue; }
					long started = System.Diagnostics.Stopwatch.GetTimestamp();
					try { Models[i] = Model(Profiles[i], Frame, paths[i], Content[i], Period, MaxFrames, MaxEvents); }
					catch (Exception e) { Failures[i] = e; }
					Seconds[i] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
				}
			}
			finally { m_ready = true; }
		}

		internal void Start() => new System.Threading.Thread(Run) { IsBackground = true, Name = "OfflineWake", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();

		internal bool Matches(byte[][] sections) => Sections != null && sections.Length == Sections.Length
			&& sections.Zip(Sections).All(x => x.First.AsSpan().SequenceEqual(x.Second));
	}

	/// <summary>Game thread. Throws, as a synchronous wake did, when the authority clock or a profile's timebase fails;
	/// null when there is nothing to compensate.</summary>
	internal static Wake BeginWake(OfflineProfile[] profiles, byte[][] sections, int[] section)
	{
		if (!HostSettings.GetBool("offline.compensate") || profiles.Length == 0) return null;
		var authority = Host.Cluster ? Host.AuthorityClock() : default;
		// Validate every timebase before any group can commit or thaw. A Skipped
		// result is not a safe response to ambiguous entitlement coordinates.
		foreach (var profile in profiles) OfflineInterval.ValidateSettlement(profile, Host.Cluster, authority);
		return new Wake
		{
			Sections = sections, Section = section, Profiles = profiles, Authority = authority, Frame = Host.Cluster ? authority.Frame : Frame,
			Rotating = MySession.Static.Settings.EnableSunRotation, Period = SunPeriodFrames, MaxFrames = MaxFrames, MaxEvents = MaxEvents,
		};
	}

	/// <summary>Game thread, once the wake is <see cref="Wake.Ready"/>. <paramref name="members"/> is each profile's recovery cover.</summary>
	internal static void Apply(IDictionary<long, MyObjectBuilder_EntityBase> entities, Wake wake, HashSet<long>[] members)
	{
		var profiles = wake.Profiles;
		foreach (var profile in profiles) ValidateBatteryModes(profile, entities);
		if (wake.PathFailure != null) ExceptionDispatchInfo.Throw(wake.PathFailure);
		var admitted = new List<int>();
		for (int i = 0; i < profiles.Length; i++)
		{
			var profile = profiles[i];
			try
			{
				if (profile.Members.Any(id => !members[i].Contains(id) || !entities.ContainsKey(id)))
					throw new InvalidDataException("incomplete logical group in recovery cover");
				if (wake.ContentFailures[i] != null) ExceptionDispatchInfo.Throw(wake.ContentFailures[i]);
				if (!wake.Content[i]) throw new InvalidDataException("content mismatch");
				admitted.Add(i);
			}
			catch (Exception e) { Skipped(profile, e); }
		}
		// Ordered paths can require more events than a scalar clock. Check all schema3
		// results before native writes, so none thaws a partially computed interval.
		// Any failure but bad data fails the load and keeps the unpaid Final for a retry.
		foreach (int i in admitted)
			if (profiles[i].Schema == OfflineProfile.ClusterSchema && wake.Failures[i] is { } failure and not InvalidDataException)
				ExceptionDispatchInfo.Throw(failure);
		foreach (int i in admitted)
		{
			try
			{
				if (wake.Failures[i] != null) ExceptionDispatchInfo.Throw(wake.Failures[i]);
				var (model, result) = wake.Models[i];
				result.WakeClockGeneration = Host.Cluster ? wake.Authority.Generation : 0;
				Commit(profiles[i], model, entities);
				Results[profiles[i].Group] = result;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline compensation " + JsonSerializer.Serialize(result, OfflineProfile.Json));
			}
			catch (Exception e)
			{
				Skipped(profiles[i], e);
			}
		}
		LogWake(wake);
	}

	// SE-1096: what a slow wake spent its worker thread on, readable from the node log.
	private static void LogWake(Wake wake)
	{
		if (wake.Seconds == null) return;
		var groups = Enumerable.Range(0, wake.Profiles.Length).Where(i => wake.Models[i].Result != null).ToArray();
		if (groups.Length == 0) return;
		int longest = groups.MaxBy(i => wake.Seconds[i]);
		var capped = groups.Where(i => wake.Models[i].Result.Capped && wake.Models[i].Result.Events >= wake.MaxEvents).Select(i => wake.Profiles[i].Group).ToArray();
		MyLog.Default?.WriteLineAndConsole(FormattableString.Invariant($"[ClusterPlugin] offline compensation wake: {groups.Length} groups, {groups.Sum(i => (long)wake.Models[i].Result.Events)} events, model {groups.Sum(i => wake.Seconds[i]):F2} s; longest group={wake.Profiles[longest].Group} events={wake.Models[longest].Result.Events} {wake.Seconds[longest]:F2} s")
			+ (capped.Length == 0 ? "" : $"; capped by offline.compensateMaxEvents={wake.MaxEvents}: groups {string.Join(",", capped)}"));
	}

	private static void Skipped(OfflineProfile profile, Exception error)
	{
		Results[profile.Group] = new() { Group = profile.Group, FromFrame = profile.SimulatedThrough, Skipped = error.Message, Confidence = "Frozen",
			ClockDomain = profile.ClockDomain, ClockEpoch = profile.ClockEpoch, ClockGeneration = profile.ClockGeneration };
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline compensation skipped group=" + profile.Group + ": " + error.Message);
	}

	internal static (OfflineProfile Model, OfflineResult Result) ModelForHost(OfflineProfile profile, long wake)
		=> ModelWithPath(profile, wake, ReadSunPath(profile, wake));

	private static SunWorkPath ReadSunPath(OfflineProfile profile, long wake) => ReadSunPath(profile, wake, MySession.Static.Settings.EnableSunRotation, MaxFrames);

	// No session state: a wake reads its paths on its worker thread.
	private static SunWorkPath ReadSunPath(OfflineProfile profile, long wake, bool rotating, long maxFrames)
	{
		OfflineModel.ValidateClock(profile);
		if (profile.Schema != OfflineProfile.ClusterSchema) return null;
        if (!rotating || !profile.Sources.Any(s => s.Curve is { Length: > 1 }) || wake <= profile.SimulatedThrough || maxFrames == 0) return null;
		long through = checked(profile.SimulatedThrough + Math.Min(wake - profile.SimulatedThrough, maxFrames));
		return SunWorkPath.Read(Host.SunPath(profile.ClockDomain, profile.ClockEpoch, profile.SimulatedThrough, through), profile.ClockEpoch, profile.SimulatedThrough, through);
	}

	private static double SunPeriodFrames => MySession.Static.Settings.EnableSunRotation ? MySession.Static.Settings.SunRotationIntervalMinutes * 60000.0 / 16 : 0;

	private static (OfflineProfile Model, OfflineResult Result) ModelWithPath(OfflineProfile profile, long wake, SunWorkPath path)
		=> Model(profile, wake, path, profile.ContentHash == ContentHash(profile.Definitions), SunPeriodFrames, MaxFrames, MaxEvents);

	// No session state: a wake runs its models on its worker thread.
	private static (OfflineProfile Model, OfflineResult Result) Model(OfflineProfile profile, long wake, SunWorkPath path, bool content, double period, long maxFrames, int maxEvents)
	{
		if (!content) throw new InvalidDataException("content mismatch");
		var model = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(profile, OfflineProfile.Json), OfflineProfile.Json);
		if (model.SunPeriodFrames != period)
		{
			model.SunPeriodFrames = period;
			if (model.Confidence == "Exact") model.Confidence = "Approximate";
		}
		model.SunPath = path;
		return (model, OfflineModel.Simulate(model, wake, maxFrames, maxEvents));
	}

	internal static object Status() => new { enabled = HostSettings.GetBool("offline.compensate"), results = Results.Values.ToArray(), profiling = ProfilingStatus(), cache = OfflineRateCache.Status() };

	internal static OfflineResult[] DryRun(byte[] profiles, double hours)
	{
		if (!double.IsFinite(hours) || hours < 0 || hours > 168) throw new ArgumentException("hours must be between 0 and 168");
		if (profiles == null) return [];
		return (JsonSerializer.Deserialize<OfflineProfile[]>(profiles, OfflineProfile.Json) ?? []).Select(p =>
		{
			OfflineModel.ValidateClock(p);
			if (p.ContentHash != ContentHash(p.Definitions)) return new OfflineResult { Group = p.Group, Skipped = "content mismatch", Confidence = "Frozen" };
			long wake = checked(p.SimulatedThrough + (long)(hours * FramesPerAuthorityHour));
			SunWorkPath path;
			try
			{
				path = ReadSunPath(p, wake);
			}
			catch (Exception e) when (p.Schema == OfflineProfile.ClusterSchema && e is InvalidDataException or InvalidOperationException or System.Net.Http.HttpRequestException)
			{
				// A requested future sun/work path is not evidence of an actual
				// interval. Use the existing skipped-result contract, never invent one.
				return new OfflineResult { Group = p.Group, FromFrame = p.SimulatedThrough, Skipped = "WA sun/work prediction unavailable: " + e.Message,
					Confidence = "Frozen", ClockDomain = p.ClockDomain, ClockEpoch = p.ClockEpoch, ClockGeneration = p.ClockGeneration };
			}
			return ModelWithPath(p, wake, path).Result;
		}).ToArray();
	}

	private static MySession m_contentSession;
	private static Task<string> m_modContent;
	internal static bool ContentReady
	{
		get
		{
			if (!ReferenceEquals(m_contentSession, MySession.Static) || m_modContent == null)
			{
				m_contentSession = MySession.Static;
				m_contentHashes.Clear();
				m_fingerprints.Clear();
				string[] paths = MySession.Static.Mods.Select(m => m.GetPath()).ToArray();
				m_modContent = paths.Length == 0 ? Task.FromResult(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))) : Task.Run(() =>
				{
					using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
					foreach (string root in paths)
					{
						string[] files = File.Exists(root) ? new[] { root } : Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => Path.GetRelativePath(root, f), StringComparer.Ordinal).ToArray()
							: throw new FileNotFoundException("Mod content is unavailable", root);
						foreach (string file in files)
						{
							hash.AppendData(Encoding.UTF8.GetBytes(File.Exists(root) ? Path.GetFileName(root) : Path.GetRelativePath(root, file).Replace('\\', '/')));
							using var input = File.OpenRead(file);
							hash.AppendData(SHA256.HashData(input));
						}
					}
					return Convert.ToHexString(hash.GetHashAndReset());
				});
			}
			return m_modContent.IsCompleted;
		}
	}

	// Definitions and mods do not change while a session runs, but the reflection walk over them took
	// about 15 s per sweep for a 207-ship partition (SE-1299). Fingerprints are cached per definition
	// and whole hashes per settings and definition set, both until the session changes.
	private static readonly ConcurrentDictionary<string, byte[]> m_fingerprints = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, string> m_contentHashes = new(StringComparer.Ordinal);

	internal static string ContentHash(IEnumerable<string> definitions)
	{
		using var stream = new MemoryStream();
		void Write(byte[] bytes) { stream.Write(BitConverter.GetBytes(bytes.Length)); stream.Write(bytes); }
		void Append(string text) => Write(Encoding.UTF8.GetBytes(text ?? string.Empty));
		if (!ContentReady) throw new InvalidOperationException("Mod content hash is still being prepared");
		Append(m_modContent.GetAwaiter().GetResult());
		Append(MyFinalBuildConstants.APP_VERSION_STRING.ToString());
		Append(MySector.SunProperties.BaseSunDirectionNormalized.ToString());
		Append(MySector.SunProperties.SunRotationAxis.ToString());
		Append(MySession.Static.Settings.EnableSunRotation.ToString());
		var session = MySession.Static;
		Append(string.Join("|", session.Mods.Select(m => m.PublishedFileId + ":" + m.PublishedServiceName + ":" + m.Name)));
		Append(string.Join("|", session.RefinerySpeedMultiplier.ToString("R", CultureInfo.InvariantCulture),
			session.AssemblerSpeedMultiplier.ToString("R", CultureInfo.InvariantCulture), session.AssemblerEfficiencyMultiplier.ToString("R", CultureInfo.InvariantCulture),
			session.Settings.BlocksInventorySizeMultiplier.ToString("R", CultureInfo.InvariantCulture), session.CreativeMode, session.SimplifiedSimulation));
		long header = stream.Length;
		string[] ids = definitions.OrderBy(x => x, StringComparer.Ordinal).ToArray();
		foreach (string id in ids) Append(id);
		string key = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
		if (m_contentHashes.TryGetValue(key, out string cached)) return cached;
		stream.SetLength(header);
		foreach (string id in ids)
		{
			Append(id);
			Write(m_fingerprints.GetOrAdd(id, i => Encoding.UTF8.GetBytes(DefinitionFingerprint(
				MyDefinitionManager.Static.GetDefinition(MyDefinitionId.Parse(i)) ?? throw new InvalidDataException("Missing definition " + i)))));
		}
		return m_contentHashes[key] = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
	}

	// Definition builders do not all serialize their runtime fields (notably fuel/blueprint data).
	// Walk public data deterministically; skip callbacks and break reference cycles.
	private static string DefinitionFingerprint(object value)
	{
		var text = new StringBuilder();
		var path = new HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
		void Visit(object item, int depth)
		{
			if (item == null) { text.Append("null;"); return; }
			Type type = item.GetType();
			text.Append(type.FullName).Append(':');
			if (item is string str)
			{
				string content = VRage.FileSystem.MyFileSystem.ContentPath;
				if (!string.IsNullOrEmpty(content) && str.StartsWith(content, StringComparison.Ordinal)) str = "$CONTENT/" + str.Substring(content.Length).Replace('\\', '/');
				foreach (var mod in MySession.Static.Mods)
				{
					string root = mod.GetPath();
					if (!string.IsNullOrEmpty(root) && str.StartsWith(root, StringComparison.Ordinal)) { str = "$MOD/" + mod.PublishedFileId + "/" + str.Substring(root.Length).Replace('\\', '/'); break; }
				}
				text.Append(str.Length).Append(':').Append(str); return;
			}
			if (type.IsPrimitive || type.IsEnum || item is decimal || item is MyDefinitionId || item is MyFixedPoint)
			{ text.Append(Convert.ToString(item, CultureInfo.InvariantCulture)).Append(';'); return; }
			if (depth > 12 || item is Delegate || !type.IsValueType && !path.Add(item)) { text.Append("cycle;"); return; }
			if (item is System.Collections.IDictionary dictionary)
			{
				foreach (var key in dictionary.Keys.Cast<object>().OrderBy(k => k.ToString(), StringComparer.Ordinal)) { Visit(key, depth + 1); Visit(dictionary[key], depth + 1); }
			}
			else if (item is System.Collections.IEnumerable sequence) foreach (var child in sequence) Visit(child, depth + 1);
			else
			{
				foreach (var member in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Where(f => f.Name is not ("Context" or "ModContext")).OrderBy(f => f.Name, StringComparer.Ordinal))
				{ text.Append(member.Name).Append('='); Visit(member.GetValue(item), depth + 1); }
				foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
					.Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)))
					.OrderBy(p => p.Name, StringComparer.Ordinal))
				{ text.Append(property.Name).Append('='); Visit(property.GetValue(item), depth + 1); }
			}
			if (!type.IsValueType) path.Remove(item);
		}
		Visit(value, 0);
		return text.ToString();
	}

	internal static IEnumerable<MyObjectBuilder_Inventory> Inventories(MyObjectBuilder_CubeBlock block)
	{
		IEnumerable<MyObjectBuilder_Inventory> Flatten(MyObjectBuilder_InventoryBase inventory)
		{
			if (inventory is MyObjectBuilder_Inventory single) yield return single;
			if (inventory is MyObjectBuilder_InventoryAggregate aggregate)
				foreach (var child in aggregate.Inventories ?? []) foreach (var nested in Flatten(child)) yield return nested;
		}
		var components = block.ComponentContainer?.Components;
		var found = (components ?? []).Where(c => c.Component is MyObjectBuilder_InventoryBase)
			.SelectMany(c => Flatten((MyObjectBuilder_InventoryBase)c.Component)).ToArray();
		if (found.Length != 0) return found;
		// Older saves used the block's legacy fields; the component container is authoritative when present.
		if (block is MyObjectBuilder_ProductionBlock production) return new[] { production.InputInventory, production.OutputInventory }.Where(i => i != null);
		if (block is MyObjectBuilder_Reactor reactor && reactor.Inventory != null) return new[] { reactor.Inventory };
		if (block is MyObjectBuilder_OxygenGenerator generator && generator.Inventory != null) return new[] { generator.Inventory };
		if (block is MyObjectBuilder_GasTank tank && tank.Inventory != null) return new[] { tank.Inventory };
		return [];
	}

	private static void ValidateBatteryModes(OfflineProfile profile, IDictionary<long, MyObjectBuilder_EntityBase> entities)
	{
		if (profile.Creative) return; // Existing whole-world Creative policy is separate.
		var batteries = profile.Members.SelectMany(id => entities.TryGetValue(id, out var entity) && entity is MyObjectBuilder_CubeGrid grid
			? grid.CubeBlocks.OfType<MyObjectBuilder_BatteryBlock>().Select(b => (Grid: grid, Battery: b))
			: Enumerable.Empty<(MyObjectBuilder_CubeGrid Grid, MyObjectBuilder_BatteryBlock Battery)>()).ToDictionary(b => b.Battery.EntityId);
		foreach (var store in profile.Storage.Where(s => s.Kind == "Battery"))
		{
			if (!batteries.TryGetValue(store.Block, out var saved)) throw new InvalidDataException("Battery absent from saved group");
			var b = saved.Battery;
			// Same predicate as MyTerminalBlock.IsCreativeModeEnabled, evaluated on
			// the immutable builders before Cluster instantiates any native entity.
			bool creative = MySession.Static.CreativeMode || saved.Grid.IsNpcSpawnedGrid && !b.IsSurvivalModeForced
				&& Sandbox.Game.Multiplayer.Sync.Players.IdentityIsNpc(b.Owner)
				&& (b.Owner == b.BuiltBy || Sandbox.Game.Multiplayer.Sync.Players.IdentityIsNpc(b.BuiltBy));
			if (store.Creative != creative) throw new InvalidDataException("Battery creative mode missing or changed; retaining unpaid profile");
		}
	}

	/// <summary>Validate and prepare every write before touching any builder. IDs, stacks and all
	/// item metadata that the model does not own survive; new products receive fresh inventory IDs.</summary>
	internal static void Commit(OfflineProfile before, OfflineProfile after, IDictionary<long, MyObjectBuilder_EntityBase> entities)
	{
		ValidateBatteryModes(before, entities);
		var blocks = before.Members.SelectMany(id => ((MyObjectBuilder_CubeGrid)entities[id]).CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
		var writes = new List<Action>();
		for (int i = 0; i < after.Inventories.Count; i++)
		{
			var original = before.Inventories[i]; var modeled = after.Inventories[i];
			if (!blocks.TryGetValue(modeled.Block, out var block)) throw new InvalidDataException("Inventory block absent");
			var candidates = Inventories(block).ToArray();
			var inventory = candidates.FirstOrDefault(x => x.InventoryId == original.InventoryId)
				?? (original.Index < candidates.Length ? candidates[original.Index] : null)
				?? throw new InvalidDataException("Inventory absent from saved builder");
			var actual = inventory.Items.GroupBy(x => x.PhysicalContent.GetId().ToString()).ToDictionary(g => g.Key, g => g.Sum(x => (decimal)x.Amount));
			var expected = original.Stacks.GroupBy(x => x.Item).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
			if (actual.Count != expected.Count || actual.Any(x => expected.GetValueOrDefault(x.Key) != x.Value)) throw new InvalidDataException("Frozen inventory differs from profile");
			var amounts = modeled.Stacks.GroupBy(x => x.Item).ToDictionary(g => g.Key, g => decimal.Floor(g.Sum(x => x.Amount) * 1000000) / 1000000);
			decimal volume = amounts.Sum(x => x.Value * after.Items[x.Key].Volume), mass = amounts.Sum(x => x.Value * after.Items[x.Key].Mass);
			if (volume > Math.Max(modeled.MaxVolume, original.Stacks.Sum(x => x.Amount * before.Items[x.Item].Volume)) + 0.000001m
				|| mass > Math.Max(modeled.MaxMass, original.Stacks.Sum(x => x.Amount * before.Items[x.Item].Mass)) + 0.000001m)
				throw new InvalidDataException("Compensated inventory exceeds its limit");
			var items = new List<MyObjectBuilder_InventoryItem>();
			foreach (var item in inventory.Items)
			{
				string id = item.PhysicalContent.GetId().ToString();
				decimal keep = Math.Min((decimal)item.Amount, amounts.GetValueOrDefault(id));
				if (keep > 0) items.Add(new() { Amount = (MyFixedPoint)keep, PhysicalContent = item.PhysicalContent, ItemId = item.ItemId, Scale = item.Scale });
				amounts[id] -= keep;
			}
			uint next = inventory.nextItemId;
			var used = inventory.Items.Select(x => x.ItemId).ToHashSet();
			foreach (var amount in amounts.Where(x => x.Value > 0))
			{
				while (used.Contains(next)) next = checked(next + 1);
				items.Add(new() { Amount = (MyFixedPoint)amount.Value, PhysicalContent = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializerKeen.CreateNewObject(MyDefinitionId.Parse(amount.Key)), ItemId = next });
				used.Add(next); next = checked(next + 1);
			}
			uint nextId = next;
			writes.Add(() => { inventory.Items = items; inventory.nextItemId = nextId; });
		}
		foreach (var store in after.Storage)
		{
			MyObjectBuilder_CubeBlock block = blocks[store.Block];
			var basis = before.Storage.Single(s => s.Block == store.Block);
			bool matches = block switch
			{
				MyObjectBuilder_BatteryBlock b => b.CurrentStoredPower == (float)basis.Amount,
				MyObjectBuilder_GasTank t => t.FilledRatio == (float)(basis.Capacity > 0 ? basis.Amount / basis.Capacity : 0),
				MyObjectBuilder_FueledPowerProducer e => e.Capacity == (float)basis.Amount,
				MyObjectBuilder_JumpDrive j => j.StoredPower == (float)basis.Amount,
				_ => false,
			};
			if (!matches) throw new InvalidDataException("Frozen reservoir differs from profile");
			switch (store.Kind)
			{
				case "Battery": var battery = (MyObjectBuilder_BatteryBlock)block; writes.Add(() => battery.CurrentStoredPower = (float)store.Amount); break;
				case "Tank": var tank = (MyObjectBuilder_GasTank)block; writes.Add(() => tank.FilledRatio = store.Capacity > 0 ? (float)(store.Amount / store.Capacity) : 0); break;
				case "Engine": var engine = (MyObjectBuilder_FueledPowerProducer)block; writes.Add(() => engine.Capacity = (float)store.Amount); break;
				case "Jump": var jump = (MyObjectBuilder_JumpDrive)block; writes.Add(() => jump.StoredPower = (float)store.Amount); break;
			}
		}
		foreach (var source in after.Sources.Where(s => s.FuelTickFrames > 0))
		{
			var block = blocks[source.Block];
			var timer = block.ComponentContainer?.Components.Select(c => c.Component).OfType<MyObjectBuilder_TimerComponent>().SingleOrDefault()
				?? throw new InvalidDataException("Reactor fuel timer missing");
			uint counter = checked((uint)(Math.Floor(source.FuelElapsedFrames / 100) * 100));
			uint tick = checked((uint)source.FuelTickFrames);
			var grid = before.Members.Select(id => (MyObjectBuilder_CubeGrid)entities[id]).Single(g => g.CubeBlocks.Contains(block));
			writes.Add(() =>
			{
				timer.FramesFromLastTrigger = counter; timer.TimerTickInFrames = tick;
				if (source.FuelTier2AfterFrames < 0 && source.FuelTickFrames == source.FuelTier2TickFrames)
					grid.PlayerPresenceTier = MyUpdateTiersPlayerPresence.Tier2;
			});
		}
		foreach (var production in after.Production.Where(p => !p.Refinery))
		{
			var block = (MyObjectBuilder_Assembler)blocks[production.Block];
			var basis = before.Production.Single(p => p.Block == production.Block);
			var actual = (block.Queue ?? []).Select(q => (((MyDefinitionId)q.Id).ToString(), (decimal)q.Amount, q.ItemId ?? 0));
			if (block.CurrentProgress != (float)basis.Progress
				|| !actual.SequenceEqual(basis.Queue.Select(q => (q.Recipe, q.Amount, q.ItemId))))
				throw new InvalidDataException("Frozen production queue differs from profile");
			uint next = block.NextItemId;
			var queue = production.Queue.Select(q => new MyObjectBuilder_ProductionBlock.QueueItem { Id = MyDefinitionId.Parse(q.Recipe), Amount = (MyFixedPoint)q.Amount, ItemId = next++ }).ToArray();
			writes.Add(() => { block.Queue = queue; block.NextItemId = next; block.CurrentProgress = (float)production.Progress; });
		}
		foreach (var zone in after.Upkeep)
		{
			var block = (MyObjectBuilder_FunctionalBlock)blocks[zone.Block];
			var component = block.ComponentContainer?.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().FirstOrDefault()
				?? throw new InvalidDataException("Safe zone component missing");
			// Prepare every parse/serialization before committing any builder writes. Removing a
			// bubble retains its settings/rights and the functional block's Enabled switch, like
			// vanilla power loss/chip exhaustion; paid credit can reactivate after power returns.
			string carried = BlockBuilderEntry.Read(block, BlockSafeZones.Key);
			var bubble = carried == null ? null : BlockSafeZones.Parse(carried);
			var settings = component.SafeZoneOb ?? bubble;
			var retained = settings?.Clone() as MyObjectBuilder_SafeZone;
			if (retained != null) retained.Enabled = false;
			bool exists = zone.Enabled && zone.Exists;
			// An initially power-paused zone has no carried live entry. If its modeled
			// supply recovered, restore the remembered entity through the same carried-zone
			// path, with its original id/settings and the already-accounted activation clock.
			if (exists && bubble == null)
			{
				if (retained == null || retained.EntityId == 0 || retained.SafeZoneBlockId != block.EntityId || entities.ContainsKey(retained.EntityId))
					throw new InvalidDataException("Paused safe zone settings missing or conflicting");
				bubble = (MyObjectBuilder_SafeZone)retained.Clone();
			}
			if (bubble != null) bubble.Enabled = exists && zone.ActivationRemainingFrames <= 0;
			string nextEntry = exists && bubble != null ? BlockSafeZones.Serialize(bubble) : null;
			long nextZoneId = exists ? bubble.EntityId : 0;
			writes.Add(() =>
			{
				component.UpkeepTime = zone.RemainingFrames * (1000.0 / 60);
				component.Activating = exists && zone.ActivationRemainingFrames > 0;
				component.ActivationTime = checked((long)Math.Ceiling(zone.ActivationRemainingFrames * (1000.0 / 60)));
				component.SafeZoneOb = retained;
				((ObjectBuilders.SafeZone.MyObjectBuilder_SafeZoneBlock)block).SafeZoneId = nextZoneId;
				BlockBuilderEntry.Write(block, BlockSafeZones.Key, nextEntry);
			});
		}
		foreach (var write in writes) write();
	}
}
