#!/usr/bin/env python3
"""Cold stock comparison boundary checks; serializer/native lifecycle are tested in the DS lane."""
from pathlib import Path
import hashlib,json,subprocess,sys
root=Path(__file__).resolve().parents[2]
source=Path(sys.argv[2]) if len(sys.argv)>2 else root/'ServerPlugin/AccountingStock.cs'
s=source.read_text()
a=s.index('    // Durable checkpoint contract:') if '// Durable checkpoint contract:' in s else s.index('    internal static string Fingerprint(')
b=s.index('    internal static Dictionary<long, MyObjectBuilder_EntityBase> Builders',a)
methods=s[a:b]
if 'TransactionFingerprint(' not in methods:
 methods+='\ninternal static string TransactionFingerprint(IEnumerable<MyObjectBuilder_CubeGrid> grids) => Fingerprint(grids);\n'
if 'ZoneUpkeep(' not in methods:
 methods+='\ninternal static (TimeSpan Deadline,double Serialized) ZoneUpkeep(double credit,long now) => throw new NotSupportedException("Old subject has no separate native credit expectation");\n'
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
(out/'Check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><NoWarn>CS0649</NoWarn></PropertyGroup></Project>')
(out/'Subject.cs').write_text('using System.Globalization; using System.Security.Cryptography; using System.Text; class Subject { internal static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b)); internal static string Xml(MyObjectBuilder_Base b)=>System.Text.Json.JsonSerializer.Serialize(b,b.GetType(),new System.Text.Json.JsonSerializerOptions{IncludeFields=true});\n'+methods+'}\n')
for name in ('Stubs.cs','Checks.cs'):(out/name).write_bytes((Path(__file__).parent/name).read_bytes())
(out/'source.json').write_text(json.dumps({'source':str(source),'sha256':hashlib.sha256(s.encode()).hexdigest(),'boundary':'Exact production fingerprint/zone comparison methods; inert builder/serializer collaborators, no native lifecycle claim.'},indent=2))
subprocess.run(['dotnet','run','--project',str(out/'Check.csproj'),'-c','Release'],check=True)
