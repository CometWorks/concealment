#!/usr/bin/env python3
"""Content hash caching checks (SE-1299): the exact production ContentHash methods on inert definition/session stubs."""
from pathlib import Path
import hashlib, json, subprocess, sys

root = Path(__file__).resolve().parents[2]
source = Path(sys.argv[2]) if len(sys.argv) > 2 else root / "ServerPlugin/Accounting/AccountingEngine.cs"
s = source.read_text()
a = s.index("\tprivate static MySession m_contentSession;")
b = s.index("\tinternal static IEnumerable<MyObjectBuilder_Inventory> Inventories(", a)
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=False)
(out / "Check.csproj").write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>'
    "<ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><Nullable>disable</Nullable><NoWarn>CS0649;CS0414</NoWarn></PropertyGroup></Project>"
)
(out / "Subject.cs").write_text(
    "using System.Collections.Concurrent; using System.Globalization; using System.Security.Cryptography; using System.Text;\n"
    "static class Subject {\n" + s[a:b] + "}\n"
)
for name in ("Stubs.cs", "Checks.cs"):
    (out / name).write_bytes((Path(__file__).parent / name).read_bytes())
(out / "source.json").write_text(
    json.dumps({"source": str(source), "sha256": hashlib.sha256(s.encode()).hexdigest(),
                "boundary": "Exact production content hash methods; inert definition and session stubs, no native lifecycle claim."}, indent=2)
)
subprocess.run(["dotnet", "run", "--project", str(out / "Check.csproj"), "-c", "Release"], check=True)
