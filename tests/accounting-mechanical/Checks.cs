using System.Collections.Concurrent;
new Subject().Run();
partial class Subject {
 bool applying;
 readonly Plugin plugin=new(); readonly ConcurrentDictionary<long,Record> records=new(); readonly ConcurrentDictionary<long,string> opaque=new(); static Subject Current;
 static int checks;
 void RunCase(string name,Action<MyCubeGrid[],MyObjectBuilder_CubeGrid[],MyMechanicalConnectionBlockBase,MyAttachableTopBlockBase> alter,bool accept=false,bool piston=false){
  Current=this;MySession.Static=new();MySandboxGame.Static=new();MyCubeGridGroups.Static=new();MyEntities.Entities.Clear();records.Clear();opaque.Clear();AccountingStock.World="world";AccountingStock.Stock="stock";
  var grids=new[]{new MyCubeGrid{EntityId=10},new MyCubeGrid{EntityId=11}};
  foreach(var g in grids){MyEntities.Entities[g.EntityId]=g;MyCubeGridGroups.Static.Physical.Add(g);MyCubeGridGroups.Static.Logical.Add(g);}
  MyMechanicalConnectionBlockBase basis=piston?new Piston():new MyMechanicalConnectionBlockBase();basis.EntityId=20;basis.CubeGrid=grids[0];basis.Target=21;var top=new MyAttachableTopBlockBase{EntityId=21,CubeGrid=grids[1]};grids[0].Blocks.Add(basis);grids[1].Blocks.Add(top);MyEntities.Entities[20]=basis;MyEntities.Entities[21]=top;
  var saved=new[]{new MyObjectBuilder_CubeGrid{EntityId=10,CubeBlocks=[new MyObjectBuilder_MechanicalConnectionBlock{EntityId=20,TopBlockId=21}]},new MyObjectBuilder_CubeGrid{EntityId=11,CubeBlocks=[new MyObjectBuilder_AttachableTopBlockBase{EntityId=21,ParentEntityId=20}]}};
  var row=new Record{Root=10,World="world",Members=[10,11],StockHash="stock",Poses=[[0],[0]],Epoch="unchanged",Profiles=[new(){Members=[10,11]}]};records[10]=row;records[11]=row;int calls=0;
  basis.Attach=()=>{calls++;basis.TopBlock=top;top.Stator=basis;basis.IsAttached=true;MyCubeGridGroups.Static.Physical.Add(grids);MyCubeGridGroups.Static.Logical.Add(grids);};
  alter(grids,saved,basis,top);bool success=true;try{RestoreMechanicalLinks(grids.Select((g,i)=>(g,row,saved[i])).ToArray(), new MechanicalPhysics());}catch(InvalidDataException){success=false;}
  if(success!=accept)throw new Exception(name+" wrong disposition");checks++;
  if(row.Epoch!="unchanged"||row.Profiles.Length!=1)throw new Exception(name+" changed claim");checks++;
  if(accept&&piston&&((MyPistonBase)basis).Subpart3==null)throw new Exception(name+" missing native subpart prerequisite");
  if(accept&&calls!=1)throw new Exception(name+" missing native invocation");
 }
 public void Run(){
  RunCase("complete rotor",(_,_,_,_)=>{},true);RunCase("complete piston",(_,_,_,_)=>{},true,true);
  RunCase("missing saved top",(_,s,_,_)=>s[1].CubeBlocks.Clear());
  RunCase("changed saved top",(_,s,_,_)=>((MyObjectBuilder_MechanicalConnectionBlock)s[0].CubeBlocks[0]).TopBlockId=99);
  RunCase("nonreciprocal parent",(_,s,_,_)=>((MyObjectBuilder_AttachableTopBlockBase)s[1].CubeBlocks[0]).ParentEntityId=99);
  RunCase("live target changed",(_,_,b,_)=>b.Target=99);
  RunCase("foreign top parent",(_,_,_,t)=>t.Stator=new());
  RunCase("foreign closure",(g,_,_,_)=>MyCubeGridGroups.Static.Physical.Add(g[0],new MyCubeGrid{EntityId=99}));
  RunCase("stale grid",(g,_,_,_)=>MyEntities.Entities[10]=new MyCubeGrid{EntityId=10});
  RunCase("stale block",(_,_,_,_)=>MyEntities.Entities[20]=new MyMechanicalConnectionBlockBase{EntityId=20});
  RunCase("unloading",(_,_,_,_)=>MySession.Static.IsUnloading=true);
  RunCase("foreign owner",(_,_,_,_)=>Current=new());
  RunCase("wrong game thread",(_,_,_,_)=>MySandboxGame.Static.UpdateThread=new Thread(()=>{}));
  RunCase("opaque claim",(_,_,_,_)=>opaque[10]="original");
  RunCase("changed stock",(_,_,_,_)=>AccountingStock.Stock="changed");
  RunCase("changed pose",(g,_,_,_)=>g[0].WorldMatrix=new(){Value=1});
  RunCase("native call fails",(_,_,b,_)=>b.Attach=()=>{});
  RunCase("callback changes owner",(_,_,b,_)=>{var a=b.Attach;b.Attach=()=>{a();Current=new();};});
  RunCase("callback changes stocks",(_,_,b,_)=>{var a=b.Attach;b.Attach=()=>{a();AccountingStock.Stock="changed";};});
  RunCase("callback changes pose",(g,_,b,_)=>{var a=b.Attach;b.Attach=()=>{a();g[0].WorldMatrix=new(){Value=1};};});
  RunCase("callback changes block",(_,_,b,_)=>{var a=b.Attach;b.Attach=()=>{a();MyEntities.Entities[20]=new();};});
  Console.WriteLine($"PASS {checks} exact saved-link recovery/identity/closure/stock/pose/owner guards; inert native collaborators");
 }
}
