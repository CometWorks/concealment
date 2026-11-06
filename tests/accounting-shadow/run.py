#!/usr/bin/env python3
"""Solar shadow allowance after a JSON round trip, and recovery Apply with one bad profile (SE-1151).

Usage: python3 tests/accounting-shadow/run.py /absolute/new/evidence-directory
Compiles the exact production model and the exact production Apply/Skipped/LogWake methods. The
wake is built by the test like Wake.Run builds it; native commit and battery checks are inert stubs. It does not launch Space Engineers.
"""

from pathlib import Path
import shutil, subprocess, sys

repo = Path(__file__).resolve().parents[2]
sources = repo / "ServerPlugin/Accounting"
engine = (sources / "AccountingEngine.cs").read_text()


def method(start):
    a = engine.index(start)
    depth = 0
    i = engine.index("{", a)
    while True:
        depth += {"{": 1, "}": -1}.get(engine[i], 0)
        i += 1
        if depth == 0:
            return engine[a:i]


out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=False)
for name in (
    "OfflineProfile.cs",
    "SunWorkPath.cs",
    "OfflineModel.cs",
    "OfflineModel.Cycles.cs",
    "OfflineModel.Index.cs",
):
    shutil.copy2(sources / name, out / name)
(out / "Apply.cs").write_text(
    "using System.Runtime.ExceptionServices;\nusing System.Text.Json;\nnamespace Concealment.Accounting;\ninternal static partial class AccountingEngine\n{\n"
    + method(
        "internal static void Apply(IDictionary<long, MyObjectBuilder_EntityBase> entities, Wake wake"
    )
    + "\n"
    + method("private static void Skipped(")
    + "\n"
    + method("private static void LogWake(")
    + "\n}\n"
)
for name in ("Stubs.cs", "Program.cs"):
    shutil.copy2(Path(__file__).with_name(name), out / name)
(out / "Shadow.csproj").write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
    "<ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><NoWarn>CS0649</NoWarn></PropertyGroup></Project>"
)
subprocess.run(
    ["dotnet", "run", "--project", str(out / "Shadow.csproj"), "-c", "Release"],
    check=True,
)
