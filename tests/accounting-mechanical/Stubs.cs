using System.Collections.Concurrent;
class MySession { public static MySession Static=new(); public bool IsUnloading; }
class MySandboxGame { public static MySandboxGame Static=new(); public Thread UpdateThread=Thread.CurrentThread; }
class Plugin { public Manager Manager=new(); } class Manager { public Dynamic Dynamic=new(); } class Dynamic { public void GuardConcealedUpdates(IEnumerable<Type> types){} }
class MyCubeGrid { public long EntityId; public bool MarkedForClose,IsPreview,Suppressed; public List<Block> Blocks=new(); public IEnumerable<Block> GetFatBlocks()=>Blocks; public VRageMath.MatrixD WorldMatrix=new(); public Physics Physics=new(); }
class Physics { public VRageMath.MatrixD GetRigidBodyMatrix(VRageMath.MatrixD m)=>m; public int Offset,Center; public Body RigidBody=new(),RigidBody2; }
class Body { public void Deactivate(){} }
class Block { public long EntityId; public MyCubeGrid CubeGrid; public bool MarkedForClose; }
class MyMechanicalConnectionBlockBase:Block { public long? Target; public MyAttachableTopBlockBase TopBlock; public bool IsAttached; public Action Attach; public MyObjectBuilder_MechanicalConnectionBlock GetObjectBuilderCubeBlock()=>new(){EntityId=EntityId,TopBlockId=Target}; public void UpdateAttachState()=>Attach(); }
class Piston:MyPistonBase {}
class MyPistonBase:MyMechanicalConnectionBlockBase { public object Subpart3; public void LoadSubparts()=>Subpart3=new(); }
class MyAttachableTopBlockBase:Block { public MyMechanicalConnectionBlockBase Stator; }
class BuilderBlock { public long EntityId; }
class MyObjectBuilder_MechanicalConnectionBlock:BuilderBlock { public long? TopBlockId; }
class MyObjectBuilder_AttachableTopBlockBase:BuilderBlock { public long ParentEntityId; }
class MyObjectBuilder_CubeGrid { public long EntityId; public List<BuilderBlock> CubeBlocks=new(); public Position? PositionAndOrientation=new Position(); }
struct Position { public VRageMath.MatrixD GetMatrix()=>new(); }
class MyEntities { public static Dictionary<long,object> Entities=new(); public static object GetEntityById(long id)=>Entities.GetValueOrDefault(id); }
class Node { public MyCubeGrid NodeData; } class Group { public List<Node> Nodes; public Group(params MyCubeGrid[] grids){Nodes=grids.Select(g=>new Node{NodeData=g}).ToList();} }
class Grouping { public Dictionary<long,Group> Groups=new(); public Group GetGroup(MyCubeGrid grid)=>Groups.GetValueOrDefault(grid.EntityId); public void Add(params MyCubeGrid[] grids){var group=new Group(grids);foreach(var g in grids)Groups[g.EntityId]=group;} }
class MyCubeGridGroups { public static MyCubeGridGroups Static=new(); public Grouping Physical=new(),Logical=new(); }
class ConcealGroup { public static void SuppressUpdates(MyCubeGrid grid,object unused)=>grid.Suppressed=true; }
class OfflineProfile {}
class AccountingStock { public static string World="world",Stock="stock"; public static double[] Pose(VRageMath.MatrixD m)=>[m.Value]; public static double[][] Poses(IEnumerable<MyCubeGrid> grids)=>grids.Select(g=>Pose(g.WorldMatrix)).ToArray(); public static bool SameLoadedPoses(MyCubeGrid[] grids,double[][] saved,double[][] loaded)=>SamePoses(saved,loaded); public static bool SamePoses(double[][] a,double[][] b)=>a.Length==b.Length&&a.Zip(b).All(v=>v.First.SequenceEqual(v.Second)); public static void RestoreLoadedEngines(IEnumerable<MyCubeGrid> grids,MyObjectBuilder_CubeGrid[] builders,string hash){if(hash!=Stock)throw new InvalidDataException("stock");} public static Dictionary<long,MyObjectBuilder_CubeGrid> Builders(IEnumerable<MyCubeGrid> grids)=>grids.ToDictionary(g=>g.EntityId,g=>new MyObjectBuilder_CubeGrid{EntityId=g.EntityId}); public static string Fingerprint(IEnumerable<MyObjectBuilder_CubeGrid> grids)=>Stock; }
namespace VRageMath { struct MatrixD { public double Value; public int Translation; } static class Vector3D { public static int TransformNormal(int center,ref MatrixD matrix)=>0; } }
