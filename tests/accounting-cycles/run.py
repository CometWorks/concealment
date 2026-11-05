#!/usr/bin/env python3
"""Compare exact production model against the same sources with only cycle skipping disabled.
No native host: all fixtures here are synthetic; retained native Auto oracle is separate.
"""
import pathlib, shutil, subprocess, sys
repo = pathlib.Path(__file__).resolve().parents[2]
out = pathlib.Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=False)
sources = repo / 'ServerPlugin/Accounting'
for name in ['OfflineProfile.cs', 'SunWorkPath.cs', 'OfflineModel.cs', 'OfflineModel.Cycles.cs', 'OfflineModel.Index.cs']:
    text = (sources / name).read_text()
    (out / name).write_text(text)
    # Both models share the per-run index types of OfflineModel.Index.cs.
    if name in ('OfflineModel.cs', 'OfflineModel.Cycles.cs'):
        serial = text.replace('OfflineModel', 'SerialModel')
        if name == 'OfflineModel.cs':
            needle = 'else elapsed += cycles.Advance(profile, result, elapsed, cycleBoundary);'
            assert serial.count(needle) == 1
            serial = serial.replace(needle, '// Ordinary event evaluation: no cycle skipping.')
        (out / name.replace('OfflineModel', 'SerialModel')).write_text(serial)
shutil.copy2(repo/'tests/accounting-auto/reference.json', out/'reference.json')
shutil.copy2(pathlib.Path(__file__).with_name('Program.cs'), out/'Program.cs')
(out/'Cycles.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><None Update="reference.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup></Project>')
with (out/'run.log').open('w') as log:
    subprocess.run(['dotnet','run','--project',str(out/'Cycles.csproj'),'-c','Release'],stdout=log,stderr=subprocess.STDOUT,check=True)
print((out/'run.log').read_text())
