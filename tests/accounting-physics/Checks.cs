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
 public static void Main(){
  var (o,b)=Make();Check(o.Intact);var saved=o.Snapshot();Check(saved[20].Enabled&&!saved[21].Enabled&&!saved[22].Present);o.ValidateComplete();o.Release();Check(b[0].Constraint.Enabled&&!b[1].Constraint.Enabled&&b[2].Constraint==null);Check(!b[0].Constraint.ForceDisabled&&!b[1].Constraint.ForceDisabled);
  (o,b)=Make();o.ValidateComplete();b[1].Constraint.Enabled=true;Check(!o.Intact&&!b[1].Constraint.Enabled&&b[1].Constraint.ForceDisabled);
  (o,b)=Make();var old=b[1].Constraint;var replacement=new HkConstraint();b[1].Constraint=replacement;replacement.Enabled=true;Check(!o.Intact&&!replacement.Enabled&&!old.Enabled);o.Hold();Check(old.ForceDisabled&&replacement.ForceDisabled);
  (o,b)=Make();b[2].Constraint=new();b[2].Constraint.Enabled=true;Check(!o.Intact&&!b[2].Constraint.Enabled);
  (o,b)=Make();b[0].Constraint.IsDisposed=true;Check(!o.Intact);
  (o,b)=Make();b[0].Constraint.ForceDisabled=false;Check(!o.Intact&&b[0].Constraint.ForceDisabled);
  (o,b)=Make(true);try{o.Structural(b[0],()=>{b[0].Constraint=new();b[0].Constraint.Enabled=true;throw new InvalidOperationException();});}catch(InvalidOperationException){}Check(o.Intact&&!b[0].Constraint.Enabled&&o.Snapshot()[20].Enabled);o.ValidateComplete();o.Release();Check(b[0].Constraint.Enabled);
  (o,b)=Make(true);o.RestoreStates(saved);o.Structural(b[0],()=>{b[0].Constraint=new();b[0].Constraint.Enabled=true;});o.Structural(b[1],()=>{b[1].Constraint=new();b[1].Constraint.Enabled=true;});o.ValidateComplete();o.Release();Check(b[0].Constraint.Enabled&&!b[1].Constraint.Enabled);
  (o,b)=Make();o.ValidateComplete();b[0].Target=999;Check(!o.Intact);
  bool rejected=false;try{JsonSerializer.Deserialize<MechanicalPhysics.State>("{}",new JsonSerializerOptions{IncludeFields=true});}catch(JsonException){rejected=true;}Check(rejected);
  Console.WriteLine($"PASS {count} actual-owner state/absence/replacement/disposal/exception/durable/required-field checks; inert Havok");
 }
}
