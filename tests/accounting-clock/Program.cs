using Concealment.Accounting;
using System.Text.Json;
int checks=0;
void Check(bool condition,string why){if(!condition)throw new Exception(why);checks++;}
OfflineProfile Clone(OfflineProfile p)=>JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(p,OfflineProfile.Json),OfflineProfile.Json)!;
OfflineProfile Basis()=>new(){Group=1,Members=[1],FrozenFrame=10000,SimulatedThrough=10000,
 Storage=[new(){Block=1,Kind="Battery",Resource=OfflineModel.Electricity,Amount=1000,Capacity=1000,Enabled=true,Mode=2}],
 Sources=[new(){Block=1,Kind="Battery",Resource=OfflineModel.Electricity,Maximum=10,Storage=0,StorageCost=1d/216000}],
 Sinks=[new(){Block=2,Resource=OfflineModel.Electricity,Required=1}]};
void Refuses(OfflineProfile p,string why){string before=JsonSerializer.Serialize(p,OfflineProfile.Json);try{OfflineModel.Simulate(p,p.SimulatedThrough+225000,225000);throw new Exception(why);}catch(InvalidDataException){Check(before==JsonSerializer.Serialize(p,OfflineProfile.Json),why+" leaves evidence unchanged");}}
foreach(double oldClock in new[]{.016,1d/60}){
 var original=Basis();original.Schema=1;original.ClockFrameSeconds=oldClock;
 string evidence=JsonSerializer.Serialize(original,OfflineProfile.Json);
 foreach(long ticks in new[]{0L,160000L,72000000000L,72L*36000000000L,168L*36000000000L}){
  long units=ticks/160000,cap=72L*225000,wake=original.SimulatedThrough+units;
  var migrated=Clone(original);var result=OfflineModel.Simulate(migrated,wake,cap);
  Check(result.ClockMigration==(oldClock==.016?"schema1-0.016-to-native-frame":"schema1-native-frame"),"explicit migration result");
  Check(migrated.Schema==2&&migrated.ClockFrameSeconds==1d/60,"versioned native-frame schema");
  Check(result.Frames==Math.Min(units,cap)&&migrated.SimulatedThrough==wake,"raw world cap and actual-wake forfeiture");
  var fresh=Basis();var expected=OfflineModel.Simulate(fresh,wake,cap);
  Check(JsonSerializer.Serialize(migrated,OfflineProfile.Json)==JsonSerializer.Serialize(fresh,OfflineProfile.Json),"unpaid legacy migration equals fresh native result with unchanged phase/basis");
  if(units>0)Check(Math.Abs(result.ProducerEnergyMWh[1]-Math.Min(units,cap)/216000d)<1e-10,"one native frame per raw world unit, including 450000 (not 468750)");
  double stock=migrated.Storage[0].Amount;var repeat=OfflineModel.Simulate(Clone(migrated),wake,cap);
  Check(repeat.Frames==0&&repeat.ClockMigration==null,"paid serialization/retry does not migrate or grant twice");
  Check(evidence==JsonSerializer.Serialize(original,OfflineProfile.Json),"immutable original Final/profile retained for retry");
  var retry=Clone(original);OfflineModel.Simulate(retry,wake,cap);Check(retry.Storage[0].Amount==stock,"original-Final recomputation does not stack stock");
 }
 var advanced=Clone(original);advanced.SimulatedThrough++;Refuses(advanced,"partly modeled legacy clock is ambiguous");
}
var oneHour=Basis();var hour=OfflineModel.Simulate(oneHour,oneHour.SimulatedThrough+225000,225000);
Check(Math.Abs(hour.ProducerEnergyMWh[1]-25d/24)<1e-12,"native 1MW world hour is 1.0416666667 resource MWh");
var invalid=Basis();invalid.ClockFrameSeconds=.016;Refuses(invalid,"schema2 cannot carry legacy discriminator");
invalid=Basis();invalid.Schema=1;invalid.ClockFrameSeconds=.02;Refuses(invalid,"unknown legacy clock refused");
invalid=Basis();invalid.Schema=3;Refuses(invalid,"future schema refused");
Console.WriteLine($"PASS {checks} production-DLL native-world-clock/migration/cap/immutable-retry assertions");

JsonBoundary.Run();
