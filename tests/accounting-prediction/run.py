#!/usr/bin/env python3
"""Exercise exact prediction/preparation methods with the real detached model.

Session/settings/content/history transport are inert; this does not launch a game.
Usage: run.py NEW_OUTPUT [AccountingEngine.cs]
"""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
source = Path(sys.argv[2]) if len(sys.argv) > 2 else root / 'ServerPlugin/Accounting/AccountingEngine.cs'
text = source.read_text()
methods = text[text.index('internal static (OfflineProfile Model, OfflineResult Result) ModelForHost'):text.index('internal static object Status()')]
methods += text[text.index('internal static OfflineResult[] DryRun('):text.index('private static MySession m_contentSession;')]
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=False)
(out / 'Check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><UseSharedCompilation>false</UseSharedCompilation><NoWarn>CS0649</NoWarn></PropertyGroup></Project>')
(out / 'Subject.cs').write_text('using System.Text.Json; namespace Concealment.Accounting; static class AccountingEngine { internal const long FramesPerAuthorityHour=225000; internal static long MaxFrames=72*FramesPerAuthorityHour; internal static string ContentHash(string[] definitions)=>"current";\n' + methods + '\n}')
inputs = {str(source): hashlib.sha256(source.read_bytes()).hexdigest()}
for name in ('OfflineProfile.cs', 'OfflineModel.cs', 'OfflineModel.Cycles.cs', 'SunWorkPath.cs'):
    path = root / 'ServerPlugin/Accounting' / name
    (out / name).write_bytes(path.read_bytes())
    inputs[str(path)] = hashlib.sha256(path.read_bytes()).hexdigest()
(out / 'Checks.cs').write_bytes((Path(__file__).parent / 'Checks.cs').read_bytes())
(out / 'inputs.json').write_text(json.dumps({'inputs': inputs, 'boundary': __doc__}, indent=2) + '\n')
subprocess.run(['dotnet', 'run', '--project', str(out / 'Check.csproj'), '-c', 'Release'], check=True)
