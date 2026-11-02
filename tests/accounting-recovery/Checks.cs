using System.Collections.Concurrent;
using System.Text.Json;
partial class Subject {
 readonly List<MechanicalPhysics> physics=new();
 readonly System.Collections.Concurrent.ConcurrentDictionary<long,(MyMechanicalConnectionBlockBase Block,long? Target,MechanicalPhysics.State State)> paidPhysics=new();
 readonly HashSet<long> initializingPaidPhysics=new(); readonly HashSet<ConcealGroup> releasingPhysics=new();
 void PrunePaidPhysics(){}

 readonly ConcurrentDictionary<long,Record> records=new();
 readonly ConcurrentDictionary<long,string> opaque=new();
 readonly ConcurrentDictionary<long,(MyCubeGrid Grid,Record Record,MyObjectBuilder_CubeGrid Builder)> restored=new();
 Plugin plugin=new(); bool disposed,stopped,recovered,applying,ownSnapshot; long nextCaptureWarning; long Frame=10000;
 string CachePath="/nonexistent/accounting-recovery-cache";
 bool Enabled=true; bool Ready=>recovered&&plugin.Manager.Production.Ready;
 readonly Harmony harmony=new(); readonly List<(int Target,int Patch)> patches=[(1,2)];
 static int checks;
 static void Check(bool value,string label){if(!value)throw new Exception(label); checks++;}
 static MyCubeGrid[] Grids(params long[] ids){var gs=ids.Select(id=>new MyCubeGrid{EntityId=id}).ToArray();foreach(var g in gs)MyEntities.Grids[g.EntityId]=g;MyCubeGridGroups.Static.Physical.Add(gs);MyCubeGridGroups.Static.Logical.Add(gs);return gs;}
 static ConcealGroup Group(MyCubeGrid[] grids)=>new(MyCubeGridGroups.Static.Physical.GetGroup(grids[0]));
 static string Raw(Record record)=>JsonSerializer.Serialize(record,OfflineProfile.Json);
 Record Row(long root,long[] members,string epoch,bool profile=false)=>new(){World="world",Epoch=epoch,Root=root,Members=members,Concealed=true,CheckpointTicks=MySession.Static.ElapsedGameTime.Ticks,Profiles=profile?[new(){Tag="original-unpaid-profile",Members=members}]:null,Mechanical=profile?new():null};
 void Load(MyCubeGrid g,Record row)=>Restore(g,new(){Raw=Raw(row)});
 public void Run(){
  RootHistory();
  var history="{\"Schema\":1,\"Mechanical\":{\"9\":{\"Present\":true,\"Enabled\":true,\"ForceDisabled\":false,\"Subparts\":null}}}";
  using(var migrated=JsonDocument.Parse(MigrateMechanicalHistory(history)))Check(migrated.RootElement.GetProperty("Mechanical").GetProperty("9").GetProperty("IntentVersion").GetInt32()==1,"unforced legacy history migrates known request");
  bool refused=false;try{MigrateMechanicalHistory(history.Replace("\"ForceDisabled\":false","\"ForceDisabled\":true"));}catch(InvalidDataException){refused=true;}Check(refused,"forced legacy history refuses ambiguous intent");
  Check(MigrateMechanicalHistory("{\"Schema\":1}")=="{\"Schema\":1}","genuine no-history observation compatibility");

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
  Lifetime();
  EarlyStop();
  Console.WriteLine($"PASS {checks} exact-production recovery/admission/save/reload assertions; inert native collaborators");
 }

 void RootHistory(){
  foreach(int schema in new[]{1,2})
  foreach(string history in new[]{"missing","null","present"})
  foreach(bool stopFirst in new[]{false,true}){
   MyEntities.Grids.Clear();MyCubeGridGroups.Static=new();
   bool reject=schema==2&&history!="present";
   var grids=Grids(10,11);var unaffected=Grids(20,21);var subject=new Subject();
   var root=subject.Row(10,[10,11],"root-history-unpaid",true);root.Schema=schema;
   var node=System.Text.Json.Nodes.JsonNode.Parse(Raw(root));
   if(history=="missing")node.AsObject().Remove("Mechanical");
   if(history=="null")node["Mechanical"]=null;
   string raw=node.ToJsonString();
   subject.Restore(grids[0],new(){Raw=raw});
   var child=subject.Row(10,[10,11],root.Epoch);child.Schema=schema;subject.Load(grids[1],child);
   int links=subject.StructuralCalls;
   if(stopFirst)subject.Stop();else subject.Update();
   string label=$"schema{schema}/{history}/stop={stopFirst}";
   Check(subject.plugin.Manager.Restored.Count==(reject?0:1),label+" expected recovery verdict");
   Check(subject.StructuralCalls-links==(reject?0:1),label+" modern omission refused before structural reconstruction");
   if(reject){
    Check(subject.records[10].Mechanical==null,label+" no invented intent map on refusal");
    Check(subject.records[11].Mechanical==null,label+" child row needs no duplicate history");
    Check(subject.records.Values.All(r=>r.Rejected!=null&&r.Epoch==root.Epoch),label+" original unpaid records retained");
    Check(subject.opaque[10]==raw,label+" malformed root retained byte-exact");
    Check(grids.All(g=>g.Suppressed)&&unaffected.All(g=>!g.Suppressed),label+" entire closure held and unrelated group live");
    Check(subject.Blocks(Group(grids))&&!subject.Admission(10,1)&&!subject.TryBegin(Group(grids),()=>throw new Exception("capture")),label+" no replacement claim");
    for(int retry=0;retry<2;retry++){
     MySession.Static.ElapsedGameTime+=TimeSpan.FromTicks(160000);
     var builders=grids.Select(g=>{var b=new MyObjectBuilder_CubeGrid();subject.Save(g,b);return b;}).ToArray();
     Check(builders[0].Raw==raw,label+" save retains malformed root exactly");
     subject=new Subject();for(int i=0;i<grids.Length;i++)subject.Restore(grids[i],builders[i]);subject.Update();
     Check(subject.plugin.Manager.Restored.Count==0&&subject.Blocks(Group(grids))&&grids.All(g=>g.Suppressed),label+" reload retains refusal/closure");
     Check(subject.opaque[10]==raw,label+" reload retains original raw evidence");
     Check(subject.records[11].Epoch==root.Epoch&&subject.records[11].Rejected!=null,label+" unpaid child survives retry");
    }
   }else{
    Check(subject.records[10].Mechanical!=null&&subject.records[10].Rejected==null,label+" supported root history survives or legacy observation recorded");
   }
  }
 }

 void EarlyStop(){
  foreach(string scenario in new[]{"missing-root","missing-member","complete"})
  foreach(bool recoverFirst in new[]{false,true}){
   MyEntities.Grids.Clear();MyCubeGridGroups.Static=new();
   var grids=Grids(10,11);var unrelated=Grids(20,21);var subject=new Subject();
   subject.plugin.Manager.Accounting=subject;ConcealmentRuntime.Instance.Manager=subject.plugin.Manager;
   if(scenario!="missing-root")subject.Load(grids[0],subject.Row(10,[10,11],"early-stop-unpaid",true));
   if(scenario!="missing-member")subject.Load(grids[1],subject.Row(10,[10,11],"early-stop-unpaid"));
   if(recoverFirst)subject.Update();
   int observations=AccountingApi.Observations,restores=AccountingApi.CacheRestores,stops=AccountingApi.Stops;
   subject.Stop();subject.Stop();subject.Update();
   Check(subject.stopped&&subject.restored.Count==0,scenario+" early stop drains pending recovery");
   Check(AccountingApi.Observations==observations&&AccountingApi.CacheRestores==restores,scenario+" stop does not observe or restore rate cache");
   Check(AccountingApi.Stops==stops+1,scenario+" repeated stop cancels host exactly once");
   Check(grids.All(g=>g.Suppressed),scenario+" early stop suspends full rejected closure");
   Check(unrelated.All(g=>!g.Suppressed),scenario+" unrelated grids remain live");
   Check(!subject.TryBegin(Group(grids),()=>throw new Exception("stopped capture")),scenario+" stopped group cannot start interval");
   if(scenario=="complete")Check(subject.plugin.Manager.Restored.Count==1&&subject.records.Values.All(r=>r.Rejected==null),"complete pending group recovers without fabricated rejection");
   else Check(subject.records.Values.All(r=>r.Rejected!=null),scenario+" incomplete pending group rejected");
   for(int save=0;save<2;save++){
    MySession.Static.ElapsedGameTime+=TimeSpan.FromTicks(160000);
    var builders=grids.Select(g=>{var b=new MyObjectBuilder_CubeGrid();Saving(g,b);Check(b.Raw!=null,scenario+" stopped serializer covers each loaded member");return b;}).ToArray();
    var next=new Subject();for(int i=0;i<grids.Length;i++)next.Restore(grids[i],builders[i]);next.Update();
    Check(next.records.Values.All(r=>r.Epoch=="early-stop-unpaid"),scenario+" saved epoch preserved");
    if(scenario!="complete")Check(next.Blocks(Group(grids))&&grids.All(g=>g.Suppressed),scenario+" reload preserves group refusal");
   }
  }
 }
 void Lifetime(){
  MyEntities.Grids.Clear();MyCubeGridGroups.Static=new();
  var grids=Grids(10,11);var subject=new Subject();
  subject.plugin.Manager.Accounting=subject;ConcealmentRuntime.Instance.Manager=subject.plugin.Manager;
  var unpaid=subject.Row(10,[10,11],"lifetime-unpaid");
  grids[1].Storage.Raw=Raw(unpaid);subject.Restore(grids[1],new(){Raw=grids[1].Storage.Raw});subject.Update();
  Check(grids[1].Storage.Raw==null,"Restore moves live metadata into the world owner");
  string rejection=subject.records[11].Rejected;string evidence=subject.opaque[10];
  subject.Stop();subject.Stop();
  Check(subject.stopped&&!subject.disposed&&subject.harmony.Unpatches==0,"stop cancels jobs without unpatching serializer");
  Check(!subject.Admission(10,1)&&!subject.TryBegin(Group(grids),()=>{}),"stopped owner cannot admit another interval");
  for(int save=0;save<2;save++){
   MySession.Static.ElapsedGameTime+=TimeSpan.FromTicks(160000);
   var saved=grids.Select(g=>{var b=new MyObjectBuilder_CubeGrid();Saving(g,b);Check(b.Raw!=null,"same-world postfix still serializes every claim");return b;}).ToArray();
   Check(saved[0].Raw==evidence,"opaque evidence stays exact through later saves");
   var next=new Subject();for(int i=0;i<grids.Length;i++)next.Restore(grids[i],saved[i]);next.Update();
   Check(next.records[11].Epoch==unpaid.Epoch&&next.records[11].Rejected==rejection,"same unpaid epoch/rejection survives stopped save/reload");
   Check(next.Blocks(Group(grids))&&!next.TryBegin(Group(grids),()=>{}),"restart refuses new capture");
  }
  subject.Dispose();subject.Dispose();ConcealmentRuntime.Instance.Manager=null;
  Check(subject.disposed&&subject.harmony.Unpatches==1,"true detach removes serializer exactly once");
  var detached=new MyObjectBuilder_CubeGrid();Saving(grids[1],detached);Check(detached.Raw==null,"detached callback cannot write to another world");
 }

}
