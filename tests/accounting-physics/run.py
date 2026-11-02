#!/usr/bin/env python3
"""Actual mechanical owner, inert native fields/setters. NativeProbe qualifies Havok."""
from pathlib import Path
import hashlib,json,subprocess,sys
root=Path(__file__).resolve().parents[2]
out=Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=False)
s=root/'ServerPlugin/MechanicalPhysics.cs'
(out/'MechanicalPhysics.cs').write_bytes(s.read_bytes())
for name in ['Checks.cs','Stubs.cs']:(out/name).write_bytes((Path(__file__).parent/name).read_bytes())
(out/'Check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><NoWarn>CS0649</NoWarn></PropertyGroup></Project>')
(out/'boundary.json').write_text(json.dumps({'source':str(s),'sha256':hashlib.sha256(s.read_bytes()).hexdigest(),'scope':__doc__},indent=2))
subprocess.run(['dotnet','run','--project',str(out/'Check.csproj'),'-c','Release'],check=True)
