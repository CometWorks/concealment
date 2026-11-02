using Shared.Concealment;
using Havok;
static class Native { internal static readonly List<MechanicalPhysics> Owners=new(); }
namespace Havok {
 class HkConstraint {
  bool requested=true,forced;
  public bool IsDisposed,InWorld=true;
  public override int GetHashCode()=>IsDisposed?0:base.GetHashCode();
  public override bool Equals(object value)=>ReferenceEquals(this,value)||(IsDisposed&&value is HkConstraint c&&c.IsDisposed);
  public bool Enabled { get=>requested&&!forced; set { bool v=value; foreach(var o in Native.Owners)o.Setting(this,v,false,ref v);requested=v; } }
  public bool ForceDisabled {get=>forced;set {bool v=value;foreach(var o in Native.Owners)o.Setting(this,v,true,ref v);forced=v;} }
 }
}
namespace Sandbox.Common.ObjectBuilders { class MyObjectBuilder_MechanicalConnectionBlock { public long? TopBlockId; } }
namespace Sandbox.Game.Entities {
 class MyCubeGrid {public long EntityId;public List<Blocks.MyMechanicalConnectionBlockBase> Blocks=new();public Physics Physics=new();public IEnumerable<Blocks.MyMechanicalConnectionBlockBase> GetFatBlocks()=>Blocks;}
 class Physics {public Body RigidBody=new(),RigidBody2;}
 class Body {public void Deactivate(){} }
 class MyEntities {internal static Dictionary<long,object> Entities=new();public static object GetEntityById(long id)=>Entities.GetValueOrDefault(id);}
}
namespace Sandbox.Game.Entities.Blocks {
 class MyMechanicalConnectionBlockBase {
  public long EntityId;public bool MarkedForClose;public MyCubeGrid CubeGrid;public MyAttachableTopBlockBase TopBlock;public long? Target;
  HkConstraint constraint;
  public HkConstraint Constraint {get=>constraint;set {foreach(var o in Native.Owners.Where(o=>o.Contains(this)))o.Assign(this,value);constraint=value;} }
  public Sandbox.Common.ObjectBuilders.MyObjectBuilder_MechanicalConnectionBlock GetObjectBuilderCubeBlock()=>new(){TopBlockId=Target};
 }
 class MyAttachableTopBlockBase {public MyMechanicalConnectionBlockBase Stator;}
}
