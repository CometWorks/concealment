using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Sandbox.Common.ObjectBuilders;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;

namespace ExtractionProbe;

public sealed partial class Probe
{
    private static object DetachedCommitChecks()
    {
        var type = PluginType.Assembly.GetType("Concealment.Accounting.OfflineProfile");
        var commit = PluginType.Assembly.GetType("Concealment.Accounting.AccountingEngine")
            .GetMethod("Commit", BindingFlags.NonPublic | BindingFlags.Static);
        object Profile(string text) => JsonSerializer.Deserialize(text, type, Json);
        const string basis = "{\"Members\":[1],\"Production\":[{\"Block\":10,\"Progress\":0.25,\"Queue\":[{\"Recipe\":\"MyObjectBuilder_BlueprintDefinition/SteelPlate\",\"Amount\":2,\"ItemId\":7}]}]}";
        var before = Profile(basis);
        var after = Profile(basis.Replace("0.25", "0.5").Replace("\"Amount\":2", "\"Amount\":1"));
        var block = new MyObjectBuilder_Assembler { EntityId = 10, CurrentProgress = .25f, NextItemId = 8,
            Queue = [new() { Id = MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SteelPlate"), Amount = (MyFixedPoint)2, ItemId = 7 }] };
        var grid = new MyObjectBuilder_CubeGrid { EntityId = 1, CubeBlocks = [block] };
        var entities = new Dictionary<long, MyObjectBuilder_EntityBase> { [1] = grid };
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new InvalidOperationException("Detached commit stock/queue check failed"); checks++; }
        bool Refused(object original, object modeled)
        {
            try { commit.Invoke(null, [original, modeled, entities]); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { return true; }
            return false;
        }
        block.Queue[0].Amount = (MyFixedPoint)3;
        Check(Refused(before, after));
        Check(block.Queue[0].Amount == (MyFixedPoint)3 && block.CurrentProgress == .25f && block.NextItemId == 8);
        block.Queue[0].Amount = (MyFixedPoint)2;
        commit.Invoke(null, [before, after, entities]);
        Check(block.Queue[0].Amount == (MyFixedPoint)1 && block.CurrentProgress == .5f);
        var battery = new MyObjectBuilder_BatteryBlock { EntityId = 20, CurrentStoredPower = 1.1f };
        grid.CubeBlocks = [battery];
        const string reservoir = "{\"Members\":[1],\"Storage\":[{\"Block\":20,\"Kind\":\"Battery\",\"Capacity\":3,\"Amount\":1}]}";
        Check(Refused(Profile(reservoir), Profile(reservoir.Replace("\"Amount\":1", "\"Amount\":2"))));
        Check(battery.CurrentStoredPower == 1.1f);
        return new { passed = true, checks, scope = "Actual shared Commit with native object builders: stale queue/reservoir refused, no partial writes, matching queue accepted" };
    }
}
