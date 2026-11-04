using System;
using System.IO;
using System.Text.Json;
using Sandbox.Game.Entities;
using VRage.ObjectBuilders;

namespace Concealment.Accounting;

// Required entitlement lives in the native grid builder, alongside its stocks.
// Unknown/corrupt records remain opaque and are never interpreted as free credit.
internal static class AccountingMetadata
{
    internal static readonly Guid AccountingKey = new("d636e583-394f-4fa5-96f6-8a77dba19804");
    internal static readonly Guid LegacyKey = new("7893c060-c682-48e2-baa3-582957a31804");
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal static string Read(MyCubeGrid grid, Guid key) => grid.Storage?.TryGetValue(key, out string value) == true ? value : null;
    internal static string Blocker(MyCubeGrid grid)
    {
        foreach (var key in new[] { AccountingKey, LegacyKey })
        {
            string raw = Read(grid, key);
            if (raw == null) continue;
            try
            {
                if (raw.Length > MaximumBytes) return "oversized standalone accounting metadata";
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.GetProperty("Schema").GetInt32() != 1) return "unknown standalone accounting schema";
                if (key == LegacyKey && (root.GetProperty("RemainingFrames").GetDouble() > 0 || root.TryGetProperty("Quarantined", out var q) && q.GetBoolean())) return "legacy production credit quarantined";
                if (key == AccountingKey && root.GetProperty("Concealed").GetBoolean()) return "standalone accounting entitlement quarantined";
            }
            catch { return "unreadable standalone accounting metadata"; }
        }
        return null;
    }
    internal static void Preserve(MyCubeGrid grid, MyObjectBuilder_EntityBase builder)
    {
        foreach (var key in new[] { AccountingKey, LegacyKey })
        {
            string raw = Read(grid, key);
            if (raw != null) BlockBuilderEntry.Write(builder, key, raw);
        }
    }
}
