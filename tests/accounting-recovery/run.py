#!/usr/bin/env python3
"""Compile exact standalone recovery/admission methods with inert native collaborators.

Usage: python3 tests/accounting-recovery/run.py /absolute/new/evidence-directory
This is a production-method probe; it does not launch Space Engineers.
"""
from pathlib import Path
import hashlib, json, re, subprocess, sys
source = Path(__file__).resolve().parents[2] / 'ServerPlugin/StandaloneAccounting.cs'
s = source.read_text()
def section(start):
    a = s.index(start); brace = s.index('{', a); depth = 1; i = brace + 1
    while depth:
        if s[i] == '{': depth += 1
        elif s[i] == '}': depth -= 1
        i += 1
    return s[a:i]
parts = [section(x) for x in ('private sealed class Record', 'private void Restore(', 'private void Save(',
    'internal void Update()', 'private void RejectRecovery(', 'private static MyCubeGrid[][] Groups', 'internal bool TryBegin(')]
parts += [s[s.index('private bool HasClaim('):s.index('private static MyCubeGrid[][] Groups')].strip(),
          s[s.index('internal bool Blocks('):s.index('internal bool TryBegin(')].strip()]
admit = next(line.strip().removesuffix(',').split(' = ', 1)[1] for line in s.splitlines() if '["admit"] =' in line)
out = Path(sys.argv[1]).resolve(); out.mkdir(parents=True, exist_ok=False)
(out / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><NoWarn>CS0649</NoWarn></PropertyGroup></Project>')
(out / 'Subject.cs').write_text('using System.Collections.Concurrent;\nusing System.Text.Json;\npartial class Subject {\n' + '\n'.join(parts) + '\nFunc<ulong,uint,bool> Admission => ' + admit + ';\n}')
for name in ('Stubs.cs', 'Checks.cs'): (out / name).write_bytes((Path(__file__).parent / name).read_bytes())
(out / 'extraction.json').write_text(json.dumps({'source':str(source), 'sha256':hashlib.sha256(source.read_bytes()).hexdigest(),
    'parts':parts, 'admit':admit, 'boundary':'Exact production Restore/Save/Update/quarantine/admission/capture methods; native/cache/stock collaborators inert.'}, indent=2))
subprocess.run(['dotnet', 'run', '--project', str(out / 'Probe.csproj'), '-c', 'Release'], check=True)
