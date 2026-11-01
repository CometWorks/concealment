using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Concealment.Accounting;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;

namespace Shared.Concealment;

public sealed partial class LegacyBoostMigration
{
    private sealed class Receipt
    {
        public int Schema = 1;
        public string World, Source;
        public long Grid, CheckpointTicks;
        public double RemainingFrames;
        public bool Quarantined;
        public string Topology;
        // Merged-away rows retain their original identities and units, but cannot
        // be consumed against a different grid's stocks. Never flatten into a grant.
        public Receipt[] Retained = [];
        public string[] Opaque = [];
    }
    private sealed class Cutover
    {
        public int Schema = 1;
        public string World, Source;
        public long CheckpointTicks;
        public Dictionary<long, double> Banks = new();
        public Dictionary<long, string> InitialStocks = new();
    }
    private readonly ConcurrentDictionary<long, Receipt> receipts = new();
    private readonly ConcurrentDictionary<long, string> opaque = new();
    private readonly ConcurrentDictionary<long, string> initialStocks = new();
    private bool initialized, failed;
    private long initialCheckpointTicks = -1;
    private Cutover cutover;
    internal bool HasSavedState => !receipts.IsEmpty || !opaque.IsEmpty;
    internal bool Ready => initialized && !failed;
    internal bool Blocks(IEnumerable<MyCubeGrid> grids) => !Ready || AccountingGroup(grids).Any(g => opaque.ContainsKey(g.EntityId)
        || receipts.TryGetValue(g.EntityId, out var r) && r.Quarantined || BoostBankFrames.GetValueOrDefault(g.EntityId) > 0);

    internal void Restored(MyCubeGrid grid, MyObjectBuilder_CubeGrid builder)
    {
        Track(grid);
        Interlocked.CompareExchange(ref initialCheckpointTicks, MySession.Static.ElapsedGameTime.Ticks, -1);
        initialStocks[grid.EntityId] = AccountingStock.Fingerprint(new[] { builder });
        string raw = BlockBuilderEntry.Read(builder, AccountingMetadata.LegacyKey);
        if (raw == null) return;
        try
        {
            if (raw.Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Oversized legacy receipt");
            var receipt = JsonSerializer.Deserialize<Receipt>(raw, OfflineProfile.Json) ?? throw new InvalidDataException("Missing receipt");
            if (receipt.CheckpointTicks != MySession.Static.ElapsedGameTime.Ticks) throw new InvalidDataException("Legacy stocks and receipt belong to different world checkpoints");
            if (receipt.Schema != 1 || receipt.Grid != grid.EntityId || !ValidReceipt(receipt)) throw new InvalidDataException("Invalid legacy receipt");
            receipts[grid.EntityId] = receipt;
        }
        catch (Exception e) { opaque[grid.EntityId] = raw; log.Warning(e, "Legacy receipt quarantined for {0}", grid.EntityId); }
        grid.Storage?.RemoveValue(AccountingMetadata.LegacyKey);
    }
    private void InitializeCutover()
    {
        if (initialized || MySession.Static?.Ready != true) return;
        initialized = true;
        try
        {
            string path = banksPath + ".cutover.json";
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Oversized legacy cutover");
                cutover = JsonSerializer.Deserialize<Cutover>(File.ReadAllText(path), OfflineProfile.Json) ?? throw new InvalidDataException("Missing legacy cutover");
                if (cutover.Schema != 1 || cutover.World != AccountingStock.World) throw new InvalidDataException("Legacy cutover belongs to another world or schema");
                string provenance = banksPath + ".legacy." + cutover.Source + ".jsonl";
                if (cutover.Source != "none" && (!File.Exists(provenance) || AccountingStock.Hash(File.ReadAllBytes(provenance)) != cutover.Source)) throw new InvalidDataException("Legacy cutover source is missing or changed");
            }
            else
            {
                cutover = new Cutover { World = AccountingStock.World, Source = "none", CheckpointTicks = initialCheckpointTicks };
                string source = new[] { banksPath, banksPath + ".new", banksPath + ".bak" }.FirstOrDefault(File.Exists);
                if (source != null)
                {
                    if (new FileInfo(source).Length > AccountingMetadata.MaximumBytes) throw new InvalidDataException("Oversized legacy bank source");
                    byte[] bytes = File.ReadAllBytes(source);
                    cutover.Source = AccountingStock.Hash(bytes);
                    foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        using var doc = JsonDocument.Parse(line);
                        long id = doc.RootElement.GetProperty("gridId").GetInt64();
                        double frames = doc.RootElement.GetProperty("frames").GetDouble();
                        if (id == 0 || !double.IsFinite(frames) || frames < 0 || cutover.Banks.ContainsKey(id)) throw new InvalidDataException("Invalid/duplicate legacy bank row");
                        if (frames <= 0) continue;
                        // Freeze only members of this exact loaded world. No future arriving grid
                        // can acquire a bank merely by matching an old numeric EntityId.
                        if (initialStocks.TryGetValue(id, out string basis)) { cutover.Banks.Add(id, frames); cutover.InitialStocks.Add(id, basis); }
                    }
                    string provenance = banksPath + ".legacy." + cutover.Source + ".jsonl";
                    if (!File.Exists(provenance)) File.WriteAllBytes(provenance, bytes);
                    if (AccountingStock.Hash(File.ReadAllBytes(provenance)) != cutover.Source) throw new InvalidDataException("Legacy provenance mismatch");
                }
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(cutover, OfflineProfile.Json));
                File.Move(temp, path); // one source writer; never replace a frozen cutover
            }
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
            {
                long id = grid.EntityId;
                Track(grid);
                if (opaque.ContainsKey(id)) continue;
                if (receipts.TryGetValue(id, out var receipt))
                {
                    if (receipt.World != cutover.World || receipt.Source != cutover.Source || receipt.RemainingFrames > cutover.Banks.GetValueOrDefault(id)
                        || receipt.RemainingFrames > 0 && receipt.Topology != Topology(grid))
                    { receipt.Quarantined = true; log.Warning("Legacy receipt/source/world mismatch on {0}; credit retained", id); continue; }
                }
                else
                {
                    receipt = new Receipt { Grid = id, World = cutover.World, Source = cutover.Source, Topology = Topology(grid) };
                    if (cutover.Banks.TryGetValue(id, out double frames))
                    {
                        receipt.RemainingFrames = frames; // retain credit even when it must be quarantined
                        if (initialCheckpointTicks != cutover.CheckpointTicks || !initialStocks.TryGetValue(id, out string hash) || hash != cutover.InitialStocks.GetValueOrDefault(id)) receipt.Quarantined = true;
                    }
                    receipts[id] = receipt;
                }
                if (!receipt.Quarantined && receipt.RemainingFrames > 0)
                {
                    BoostBankFrames[id] = receipt.RemainingFrames;
                    LastConsumeTick[id] = CurrentTick;
                    EvaluateGate(grid);
                }
            }
            log.Info("Legacy cutover {0}: {1} existing positive banks; no new grants", cutover.Source, BoostBankFrames.Count);
        }
        catch (Exception e) { failed = true; log.Error(e, "Legacy credit cannot be imported safely; standalone accounting is quarantined"); }
    }
    internal void Saving(MyCubeGrid grid, MyObjectBuilder_CubeGrid builder)
    {
        if (opaque.TryGetValue(grid.EntityId, out string raw)) { BlockBuilderEntry.Write(builder, AccountingMetadata.LegacyKey, raw); return; }
        if (!initialized || failed)
        {
            if (receipts.TryGetValue(grid.EntityId, out var pending)) BlockBuilderEntry.Write(builder, AccountingMetadata.LegacyKey, JsonSerializer.Serialize(pending, OfflineProfile.Json));
            return;
        }
        if (!receipts.TryGetValue(grid.EntityId, out var receipt))
            receipts[grid.EntityId] = receipt = new Receipt { Grid = grid.EntityId, World = cutover.World, Source = cutover.Source };
        if (receipt.RemainingFrames > 0) ValidGroup(grid);
        if (!receipt.Quarantined) receipt.RemainingFrames = BoostBankFrames.GetValueOrDefault(grid.EntityId);
        var saved = new Receipt { Grid = receipt.Grid, World = receipt.World, Source = receipt.Source,
            RemainingFrames = receipt.RemainingFrames, Quarantined = receipt.Quarantined, Topology = receipt.Topology,
            Retained = receipt.Retained, Opaque = receipt.Opaque, CheckpointTicks = MySession.Static.ElapsedGameTime.Ticks };
        BlockBuilderEntry.Write(builder, AccountingMetadata.LegacyKey, JsonSerializer.Serialize(saved, OfflineProfile.Json));
    }
}
