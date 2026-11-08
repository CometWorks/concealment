// Inert stand-ins for the game types ContentHash reads.
class MyObjectBuilder_Inventory { }
struct MyFixedPoint { }
struct MyDefinitionId
{
	public string Text;
	public static MyDefinitionId Parse(string text) => new() { Text = text };
	public override string ToString() => Text;
}
class MyModContext { public string Path; }
class MyObjectBuilder_Checkpoint { public class ModItem { public ulong PublishedFileId; public string PublishedServiceName = "Steam", Name; public static string Root; public string GetPath() => Root; } }
class MySessionSettings { public bool EnableSunRotation = true; public float BlocksInventorySizeMultiplier = 1; }
class MySession
{
	public static MySession Static = new();
	public List<MyObjectBuilder_Checkpoint.ModItem> Mods = [];
	public MySessionSettings Settings = new();
	public float RefinerySpeedMultiplier = 1, AssemblerSpeedMultiplier = 1, AssemblerEfficiencyMultiplier = 1;
	public bool CreativeMode, SimplifiedSimulation;
}
class MyFinalBuildConstants { public static string APP_VERSION_STRING = "1.206.032"; }
class MySunProperties { public string BaseSunDirectionNormalized = "X:0 Y:0 Z:-1", SunRotationAxis = "X:0 Y:1 Z:0"; }
class MySector { public static MySunProperties SunProperties = new(); }
class MyDefinitionManager
{
	public static MyDefinitionManager Static = new();
	public Dictionary<string, object> Definitions = new();
	public object GetDefinition(MyDefinitionId id) => Definitions.TryGetValue(id.Text, out var d) ? d : null;
}
namespace VRage.FileSystem { static class MyFileSystem { public static string ContentPath = "/game/Content/"; } }

// A block definition with nested data, so a reflection walk costs about what a real one does.
class Part { public string Name; public float Amount; public int[] Steps = [1, 2, 3, 4]; }
class TestDefinition
{
	public static int Reads;
	public MyModContext Context = new() { Path = "/ignored" };
	public string Model = "/game/Content/Models/Cubes/Large/Battery.mwm";
	public string ModTexture = MyObjectBuilder_Checkpoint.ModItem.Root + "/Textures/a.dds";
	public List<Part> Components = Enumerable.Range(0, 40).Select(i => new Part { Name = "Steel" + i, Amount = i }).ToList();
	public Dictionary<string, double> Rates = new() { ["b"] = 2, ["a"] = 1 };
	public Action Callback = () => { };
	public int Counted { get { Interlocked.Increment(ref Reads); return 7; } }
}
