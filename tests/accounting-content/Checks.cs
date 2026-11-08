using System.Diagnostics;

// Golden hash of ids d0,d1,d2 from the uncached fingerprint (concealment 4dd2ecf); caching must not change stored hashes.
const string Golden = "44BCDE439CF94E8BC65C35353F7B8B171B2AA8E083CEA01B8C5275EA4328A4FA";
int checks = 0;
void Check(bool ok, string message) { checks++; if (!ok) throw new Exception("FAIL: " + message); }

const int Kinds = 300, Blocks = 83_632; // the SE-1299 partition: 207 shuttles, 83,632 blocks
string modRoot = Path.Combine(Path.GetTempPath(), "se1299-mod-" + Environment.ProcessId);
Directory.CreateDirectory(modRoot);
File.WriteAllText(Path.Combine(modRoot, "metadata.mod"), "mod");
MyObjectBuilder_Checkpoint.ModItem.Root = modRoot;
MySession.Static.Mods.Add(new() { PublishedFileId = 1, Name = "Mod" });
for (int i = 0; i < Kinds; i++) MyDefinitionManager.Static.Definitions["d" + i] = new TestDefinition();
string[] all = Enumerable.Range(0, Kinds).Select(i => "d" + i).ToArray();

while (!Subject.ContentReady) Thread.Sleep(1);
string golden = Subject.ContentHash(["d2", "d0", "d1"]);
Console.WriteLine("hash(d0,d1,d2) = " + golden);
Check(golden == Golden, "content hash format changed, stored profiles would all read as content mismatch");

// One sweep: per-block observed keys plus one profile hash per grid, repeated for three sweeps.
var watch = Stopwatch.StartNew();
for (int sweep = 0; sweep < 3; sweep++)
{
	for (int b = 0; b < Blocks; b++) Subject.ContentHash(["d" + b % Kinds]);
	for (int g = 0; g < 207; g++) Subject.ContentHash(all);
}
watch.Stop();
Console.WriteLine($"3 sweeps of {Blocks} block hashes + 207 profile hashes: {watch.Elapsed.TotalSeconds:F2} s, definition reads {TestDefinition.Reads}");
Check(TestDefinition.Reads == Kinds, $"each definition is fingerprinted once per session, read {TestDefinition.Reads} times for {Kinds} definitions");
Check(Subject.ContentHash(["d0", "d1", "d2"]) == golden, "cached hash equals the first one");

// A session setting is part of the hash, so it is never served from a stale entry.
MySession.Static.RefinerySpeedMultiplier = 3;
string changed = Subject.ContentHash(["d0", "d1", "d2"]);
Check(changed != golden, "a changed session setting changes the hash");
MySession.Static.RefinerySpeedMultiplier = 1;
Check(Subject.ContentHash(["d0", "d1", "d2"]) == golden, "the setting restored gives the old hash");

// A new session (world reload) fingerprints the definitions again.
int before = TestDefinition.Reads;
var mods = MySession.Static.Mods;
MySession.Static = new() { Mods = mods };
while (!Subject.ContentReady) Thread.Sleep(1);
Check(Subject.ContentHash(["d0", "d1", "d2"]) == golden && TestDefinition.Reads == before + 3, "a new session drops the cache");

bool missing = false;
try { Subject.ContentHash(["nope"]); } catch (InvalidDataException) { missing = true; }
Check(missing, "a missing definition still throws");
Directory.Delete(modRoot, true);
Console.WriteLine($"PASS: {checks} content hash checks");
