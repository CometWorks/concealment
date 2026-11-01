using System.Text.Json;
new Subject().Run();
class Plugin { public Manager Manager=new(); public Log Log=new(); }
class Manager { public Subject Accounting; public Production Production=new(); public Dynamic Dynamic=new(); public List<ConcealGroup> Restored=new(); public void RestoreConcealed(ConcealGroup g)=>Restored.Add(g); }
class Dynamic { public void GuardConcealedUpdates(IEnumerable<Type> types){} }
class Production { public bool Ready=>true; public bool Blocks(IEnumerable<MyCubeGrid> g)=>false; public void Restored(MyCubeGrid g,MyObjectBuilder_CubeGrid b){} public void Saving(MyCubeGrid g,MyObjectBuilder_CubeGrid b){} }
class Log { public int Errors; public void Error(Exception e,string s,long n){Errors++;} public void Warning(Exception e,string s,params object[] args){} }
class MySession { public static MySession Static=new(); public bool Ready=true; public TimeSpan ElapsedGameTime=TimeSpan.FromTicks(16000000); }
class MyObjectBuilder_EntityBase {}
class MyObjectBuilder_CubeGrid : MyObjectBuilder_EntityBase { public string Raw; public MyObjectBuilder_CubeGrid Clone()=>new(){Raw=Raw}; }
class OfflineProfile { public string Tag; public static JsonSerializerOptions Json=new(){IncludeFields=true}; }
class MyCubeGrid { public long EntityId; public bool MarkedForClose,IsPreview,Suppressed; public Storage Storage=new(); public object[] GetFatBlocks()=>[]; }
class Storage { public string Raw; public void RemoveValue(Guid key){ Raw=null; } }
class MyEntities { public static Dictionary<long,MyCubeGrid> Grids=new(); public static MyCubeGrid GetEntityById(long id)=>Grids.GetValueOrDefault(id); }
class Group { public List<Node> Nodes; public Group(MyCubeGrid[] grids){Nodes=grids.Select(g=>new Node{NodeData=g}).ToList();} }
class Node { public MyCubeGrid NodeData; }
class Grouping { readonly Dictionary<long,Group> groups=new(); public void Add(MyCubeGrid[] grids){var g=new Group(grids); foreach(var grid in grids)groups[grid.EntityId]=g;} public Group GetGroup(MyCubeGrid grid)=>groups.GetValueOrDefault(grid.EntityId); }
class MyCubeGridGroups { public static MyCubeGridGroups Static=new(); public Grouping Physical=new(),Logical=new(); }
class ConcealGroup { public List<MyCubeGrid> Grids; public long Id=>Grids[0].EntityId; public ConcealGroup(Group g){Grids=g.Nodes.Select(n=>n.NodeData).ToList();} public void RestoreProjectors(long[] p){} public static void SuppressUpdates(MyCubeGrid grid,object ignored)=>grid.Suppressed=true; }
class AccountingStock { public static string World="world"; public static void RestoreLoadedEngines(IEnumerable<MyCubeGrid> g,MyObjectBuilder_CubeGrid[] b,string h){} }
class AccountingMetadata { public const int MaximumBytes=16777216; public static Guid AccountingKey=Guid.NewGuid(); }
class BlockBuilderEntry { public static string Read(MyObjectBuilder_CubeGrid b,Guid key)=>b.Raw; public static void Write(MyObjectBuilder_CubeGrid b,Guid key,string raw)=>b.Raw=raw; }
class AccountingEngine { public static string Readiness(MyCubeGrid g)=>null; }
class AccountingApi { public static int Stops,Observations,CacheRestores; public static void EndStandaloneSession(){ Stops++; } public static Func<ulong,uint,bool> Admit; public static void CacheRestore(string s){CacheRestores++;} public static void UpdateObservations(){Observations++;} public static bool Prepare(ulong s,MyCubeGrid[][] groups,MyCubeGrid[] occluders,bool q)=>Admit(s,1); public static byte[] Capture(ulong s,MyCubeGrid[][] groups,MyCubeGrid[] occluders,bool q)=>JsonSerializer.SerializeToUtf8Bytes(groups.Select(g=>new OfflineProfile{Tag="new"}),OfflineProfile.Json); public static OfflineProfile[] Read(byte[] b)=>JsonSerializer.Deserialize<OfflineProfile[]>(b,OfflineProfile.Json); public static void CancelScope(ulong s){} }
class MySafeZoneBlock { public Components Components=new(); }
class Components { public T Get<T>() where T:new()=>new(); }
class MySafeZoneComponent { public void SafeZoneRemove_Server(){} }

class Harmony { public int Unpatches; public void Unpatch(int target, int patch) { Unpatches++; } }
class ConcealmentRuntime { public static ConcealmentRuntime Instance=new(); public Manager Manager; public static bool StandaloneOwner=>Instance.Manager!=null; }
