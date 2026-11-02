class MyObjectBuilder_Base { public virtual MyObjectBuilder_Base Clone()=> (MyObjectBuilder_Base)MemberwiseClone(); }
class MyObjectBuilder_SafeZone: MyObjectBuilder_Base { public long EntityId,SafeZoneBlockId; public string Name; public object PositionAndOrientation; public long[] ContainedEntities,ContainedGrids; public float Radius; public bool Enabled; public long[] Players,Factions,Entities; public string Texture; public int AllowedActions,AccessTypePlayers; }
class MyObjectBuilder_SafeZoneComponent { public MyObjectBuilder_Base SafeZoneOb; public double UpkeepTime; public bool Activating; public long ActivationTime; }
class ComponentData { public object ComponentValue; public object Component=>ComponentValue; }
class Container { public List<ComponentData> Components=[]; }
class Item { public decimal Amount; public float Scale; public MyObjectBuilder_Base PhysicalContent; }
class Inventory { public int InventoryId,InventoryFlags; public List<Item> Items=[]; }
class MyObjectBuilder_CubeGrid { public long EntityId; public List<MyObjectBuilder_CubeBlock> CubeBlocks=[]; }
class MyObjectBuilder_CubeBlock { public long EntityId; public string Min,BlockOrientation; public Container ComponentContainer; public List<Inventory> Inventories=[]; public string GetId()=>GetType().Name; }
class MyObjectBuilder_FunctionalBlock:MyObjectBuilder_CubeBlock { public bool Enabled; }
class MyObjectBuilder_BatteryBlock:MyObjectBuilder_FunctionalBlock { public float CurrentStoredPower; public int ChargeMode; public bool ProducerEnabled; }
class MyObjectBuilder_GasTank:MyObjectBuilder_FunctionalBlock { public float FilledRatio; public bool IsStockpiling; }
class MyObjectBuilder_FueledPowerProducer:MyObjectBuilder_FunctionalBlock { public float Capacity; }
class MyObjectBuilder_JumpDrive:MyObjectBuilder_FunctionalBlock { public float StoredPower; public bool Recharging; }
class QueueItem { public string Id; public decimal Amount; }
class MyObjectBuilder_ProductionBlock:MyObjectBuilder_FunctionalBlock { public QueueItem[] Queue; }
class MyObjectBuilder_Assembler:MyObjectBuilder_ProductionBlock { public float CurrentProgress; public bool RepeatAssembleEnabled,DisassembleEnabled; }
class AccountingEngine { public static IEnumerable<Inventory> Inventories(MyObjectBuilder_CubeBlock b)=>b.Inventories; }
