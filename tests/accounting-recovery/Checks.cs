using System.Collections.Concurrent;
using System.Text.Json;
partial class Subject {
 readonly ConcurrentDictionary<long,Record> records=new();
 readonly ConcurrentDictionary<long,string> opaque=new();
 readonly ConcurrentDictionary<long,(MyCubeGrid Grid,Record Record,MyObjectBuilder_CubeGrid Builder)> restored=new();
 Plugin plugin=new(); bool disposed,recovered,applying,ownSnapshot; long nextCaptureWarning; long Frame=10000;
 string CachePath="/nonexistent/accounting-recovery-cache";
 bool Enabled=true; bool Ready=>recovered&&plugin.Manager.Production.Ready;
 static int checks;
 static void Check(bool value,string label){if(!value)throw new Exception(label); checks++;}
 static MyCubeGrid[] Grids(params long[] ids){var gs=ids.Select(id=>new MyCubeGrid{EntityId=id}).ToArray();foreach(var g in gs)MyEntities.Grids[g.EntityId]=g;MyCubeGridGroups.Static.Physical.Add(gs);MyCubeGridGroups.Static.Logical.Add(gs);return gs;}
 static ConcealGroup Group(MyCubeGrid[] grids)=>new(MyCubeGridGroups.Static.Physical.GetGroup(grids[0]));
 static string Raw(Record record)=>JsonSerializer.Serialize(record,OfflineProfile.Json);
 Record Row(long root,long[] members,string epoch,bool profile=false)=>new(){World="world",Epoch=epoch,Root=root,Members=members,Concealed=true,CheckpointTicks=MySession.Static.ElapsedGameTime.Ticks,Profiles=profile?[new(){Tag="original-unpaid-profile"}]:null};
 void Load(MyCubeGrid g,Record row)=>Restore(g,new(){Raw=Raw(row)});
 public void Run(){
  foreach(string scenario in new[]{"missing-root","missing-member","mismatched-members","mismatched-epoch","topology-extra"}){
   MyEntities.Grids.Clear();MyCubeGridGroups.Static=new();
   var grids=Grids(scenario=="topology-extra"?[10,11,12]:[10,11]);
   var subject=new Subject();
   var child=subject.Row(10,scenario=="mismatched-members"?[11,12]:[10,11],"unpaid-child");
   subject.Load(grids[1],child);
   if(scenario!="missing-root" && scenario!="missing-member")subject.Load(grids[0],subject.Row(10,[10,11],scenario=="mismatched-epoch"?"unpaid-root":"unpaid-child",true));
   if(scenario=="missing-member"){
    subject.records.Clear();subject.restored.Clear();subject.Load(grids[0],subject.Row(10,[10,11],"unpaid-root",true));
   }
   var originals=subject.records.ToDictionary(p=>p.Key,p=>p.Value);
   var originalProfiles=originals.ToDictionary(p=>p.Key,p=>JsonSerializer.Serialize(p.Value.Profiles,OfflineProfile.Json));
   Check(!subject.Admission(10,1),scenario+" admission waits for recovery");
   subject.Update();subject.Update();
   var group=Group(grids);AccountingApi.Admit=subject.Admission;
   Check(subject.Blocks(group),scenario+" durable group refusal");
   Check(!subject.Admission(10,1),scenario+" root admission refuses surviving child");
   Check(!subject.TryBegin(group,()=>throw new Exception("must not freeze/capture")),scenario+" new claim refused");
   subject.Enabled=false;Check(!subject.TryBegin(group,()=>throw new Exception("pause cannot bypass")),scenario+" explicit pause cannot replace claim");subject.Enabled=true;
   Check(grids.All(g=>g.Suppressed),scenario+" entire loaded closure suspended");
   foreach(var row in originals){Check(ReferenceEquals(subject.records[row.Key],row.Value)&&row.Value.Rejected!=null,scenario+" surviving row retained/rejected");Check(JsonSerializer.Serialize(row.Value.Profiles,OfflineProfile.Json)==originalProfiles[row.Key],scenario+" no invented/changed profile");}
   for(int reload=0;reload<2;reload++){
    MySession.Static.ElapsedGameTime+=TimeSpan.FromTicks(160000);
    var saved=grids.Select(g=>{var b=new MyObjectBuilder_CubeGrid();subject.Save(g,b);Check(b.Raw!=null,scenario+" every loaded member retains evidence");return b;}).ToArray();
    subject=new Subject();for(int i=0;i<grids.Length;i++)subject.Restore(grids[i],saved[i]);subject.Update();AccountingApi.Admit=subject.Admission;
    Check(subject.Blocks(group)&&!subject.Admission(10,1)&&!subject.TryBegin(group,()=>{}),scenario+" save/reload/retry preserves refusal");
    foreach(var row in originals)Check(subject.records[row.Key].Epoch==row.Value.Epoch&&JsonSerializer.Serialize(subject.records[row.Key].Profiles,OfflineProfile.Json)==originalProfiles[row.Key],scenario+" unpaid identity survives reload");
   }
   var unaffected=Grids(20,21);AccountingApi.Admit=subject.Admission;
   Check(subject.TryBegin(Group(unaffected),()=>{}),scenario+" unaffected group independently admitted");
   Check(!subject.TryBegin(Group(unaffected),()=>{}),scenario+" outstanding complete claim cannot be replaced either");
  }
  Console.WriteLine($"PASS {checks} exact-production recovery/admission/save/reload assertions; inert native collaborators");
 }
}
