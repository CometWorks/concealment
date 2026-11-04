class Checks {
 static int count;
 static void Check(bool pass,string message){if(!pass)throw new Exception(message);count++;}
 static void Main(){
  var z=new MyObjectBuilder_SafeZone{EntityId=11,SafeZoneBlockId=4,Name="11",ContainedEntities=[5],ContainedGrids=[6],Radius=10,Players=[7],Factions=[8],Entities=[9],Texture="original",AllowedActions=3,AccessTypePlayers=1};
  var c=new MyObjectBuilder_SafeZoneComponent{SafeZoneOb=z,UpkeepTime=1000,ActivationTime=500,Activating=true};
  var b=new MyObjectBuilder_GasTank{EntityId=4,FilledRatio=.2f,ComponentContainer=new(){Components=[new(){ComponentValue=c}]}};
  var grids=new[]{new MyObjectBuilder_CubeGrid{EntityId=1,CubeBlocks=[b]}};
  string durable=Subject.Fingerprint(grids),stable=Subject.TransactionFingerprint(grids);
  z.EntityId=22;z.Name="22";z.ContainedEntities=[];z.ContainedGrids=[];z.PositionAndOrientation="native parent position";
  Check(Subject.Fingerprint(grids)!=durable,"durable hash must retain lifecycle metadata");
  Check(Subject.TransactionFingerprint(grids)==stable,"native allocation/name/membership may change");
  Check(z.EntityId==22&&z.Name=="22"&&z.PositionAndOrientation!=null,"comparison must not mutate native builder");
  void Mutation(Action change,Action reset,string label){change();Check(Subject.TransactionFingerprint(grids)!=stable,label);reset();Check(Subject.TransactionFingerprint(grids)==stable,label+" reset");}
  Mutation(()=>z.Radius=11,()=>z.Radius=10,"radius");
  Mutation(()=>z.SafeZoneBlockId=99,()=>z.SafeZoneBlockId=4,"foreign ownership");
  Mutation(()=>z.Players=[99],()=>z.Players=[7],"access rights");
  Mutation(()=>z.AllowedActions=7,()=>z.AllowedActions=3,"allowed actions");
  Mutation(()=>z.Enabled=true,()=>z.Enabled=false,"protection switch");
  Mutation(()=>c.UpkeepTime=999,()=>c.UpkeepTime=1000,"paid credit");
  Mutation(()=>c.Activating=false,()=>c.Activating=true,"activation switch");
  Mutation(()=>c.ActivationTime=499,()=>c.ActivationTime=500,"activation remainder");
  Mutation(()=>b.FilledRatio=.3f,()=>b.FilledRatio=.2f,"finite reservoir");
  string legacy=Subject.Fingerprint(grids);c.UpkeepTime--;c.ActivationTime--;
  Check(Subject.Fingerprint(grids)==legacy,"durable contract must not silently expand to component clocks");
  var credit=Subject.ZoneUpkeep(2666.98*(1000.0/60),3433);
  Check(credit.Deadline.Ticks==478826666&&credit.Serialized==44449.6666,"native credit has one tick conversion");
  Check(TimeSpan.FromMilliseconds(3433+credit.Serialized).Ticks!=credit.Deadline.Ticks,"serialized expectation must never become native input");
  Console.WriteLine($"PASS {count} stock boundary checks (native lifecycle separately qualified)");
 }
}
