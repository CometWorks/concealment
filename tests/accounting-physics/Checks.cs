using Havok;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using System.Text.Json;
namespace Shared.Concealment;
static class Checks {
 static int count;
 static void Check(bool value){count++;if(!value)throw new Exception("check "+count);}
 static (MechanicalPhysics Owner,MyMechanicalConnectionBlockBase[] Blocks) Make(bool recovery=false){
  Native.Owners.Clear();MyEntities.Entities.Clear();var grid=new MyCubeGrid{EntityId=10};
  var blocks=Enumerable.Range(0,3).Select(i=>new MyMechanicalConnectionBlockBase{EntityId=20+i,CubeGrid=grid}).ToArray();
  foreach(var b in blocks){grid.Blocks.Add(b);MyEntities.Entities[b.EntityId]=b;}
  if(!recovery){blocks[0].Constraint=new();blocks[1].Constraint=new(){Enabled=false};}
  var owner=new MechanicalPhysics(new[]{grid});Native.Owners.Add(owner);return(owner,blocks);
 }
 static (MechanicalPhysics Owner,MyPistonBase Piston) Piston(bool restored=false){
  Native.Owners.Clear();MyEntities.Entities.Clear();var grid=new MyCubeGrid{EntityId=30};
  var piston=new MyPistonBase{EntityId=40,CubeGrid=grid};grid.Blocks.Add(piston);MyEntities.Entities[40]=piston;
  if(!restored){piston.Constraint=new();piston.m_subpartsConstraint=new(){Enabled=false};}
  var owner=new MechanicalPhysics(new[]{grid});Native.Owners.Add(owner);return(owner,piston);
 }
 public static void Main(){
  var (o,b)=Make();Check(o.Intact);var saved=o.Snapshot();Check(saved[20].Enabled&&!saved[21].Enabled&&!saved[22].Present);o.ValidateComplete();o.AuthorizeRelease();o.Release();Check(b[0].Constraint.Enabled&&!b[1].Constraint.Enabled&&b[2].Constraint==null);Check(!b[0].Constraint.ForceDisabled&&!b[1].Constraint.ForceDisabled);
  (o,b)=Make();o.ValidateComplete();b[1].Constraint.Enabled=true;Check(!o.Intact&&!b[1].Constraint.Enabled&&b[1].Constraint.ForceDisabled);
  (o,b)=Make();var old=b[1].Constraint;var replacement=new HkConstraint();b[1].Constraint=replacement;replacement.Enabled=true;Check(!o.Intact&&!replacement.Enabled&&!old.Enabled);o.Hold();Check(old.ForceDisabled&&replacement.ForceDisabled);
  (o,b)=Make();b[2].Constraint=new();b[2].Constraint.Enabled=true;Check(!o.Intact&&!b[2].Constraint.Enabled);
  (o,b)=Make();b[0].Constraint.IsDisposed=true;Check(!o.Intact);o.Hold();Check(!o.Intact);
  (o,b)=Make();b[0].Constraint.ForceDisabled=false;Check(!o.Intact&&b[0].Constraint.ForceDisabled);
  (o,b)=Make(true);try{o.Structural(b[0],()=>{b[0].Constraint=new();b[0].Constraint.Enabled=true;throw new InvalidOperationException();});}catch(InvalidOperationException){}Check(o.Intact&&!b[0].Constraint.Enabled&&o.Snapshot()[20].Enabled);o.ValidateComplete();o.AuthorizeRelease();o.Release();Check(b[0].Constraint.Enabled);
  (o,b)=Make(true);o.RestoreStates(saved);o.Structural(b[0],()=>{b[0].Constraint=new();b[0].Constraint.Enabled=true;});o.Structural(b[1],()=>{b[1].Constraint=new();b[1].Constraint.Enabled=true;});o.ValidateComplete();o.AuthorizeRelease();o.Release();Check(b[0].Constraint.Enabled&&!b[1].Constraint.Enabled);
  (o,b)=Make();o.ValidateComplete();b[0].Target=999;Check(!o.Intact);
  bool rejected=false;try{JsonSerializer.Deserialize<MechanicalPhysics.State>("{}",new JsonSerializerOptions{IncludeFields=true});}catch(JsonException){rejected=true;}Check(rejected);
  var (po,piston)=Piston();po.ValidateComplete();var ps=po.Snapshot();Check(po.Intact&&ps[40].Subparts.Present&&!ps[40].Subparts.Enabled&&ps[40].Subparts.InWorld==true);po.AuthorizeRelease();po.Release();Check(!piston.m_subpartsConstraint.Enabled&&!piston.m_subpartsConstraint.ForceDisabled);
  (po,piston)=Piston();piston.m_subpartsConstraint.Enabled=true;Check(!po.Intact&&!piston.m_subpartsConstraint.Enabled);
  (po,piston)=Piston();var previous=piston.m_subpartsConstraint;piston.m_subpartsConstraint=new();po.Hold();Check(!po.Intact&&previous.ForceDisabled&&piston.m_subpartsConstraint.ForceDisabled);
  (po,piston)=Piston(true);po.RestoreStates(ps);po.Structural(piston,()=>{piston.m_subpartsConstraint=new(){InWorld=false,RigidBodyA=piston.CubeGrid.Physics.RigidBody,RigidBodyB=piston.m_subpartPhysics.RigidBody};po.Constructed(piston.m_subpartsConstraint);piston.Constraint=new();po.Assign(piston,piston.Constraint);});Check(po.Intact&&po.Snapshot()[40].Subparts.InWorld==true&&!piston.m_subpartsConstraint.InWorld);po.ValidateComplete();po.AuthorizeRelease();po.Release();Check(!piston.m_subpartsConstraint.Enabled);
  Console.WriteLine($"PASS {count} actual-owner state/absence/replacement/disposal/exception/durable/required-field checks; inert Havok");
 }
}
