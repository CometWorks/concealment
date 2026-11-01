#!/usr/bin/env python3
"""Run the exact saved-link recovery method with inert native collaborators.
NativeProbe separately proves attachment, physics, stock application and lifecycle.
"""
from pathlib import Path
import hashlib,json,subprocess,sys
root=Path(__file__).resolve().parents[2]
s=(root/'ServerPlugin/StandaloneAccounting.cs').read_text()
def extract(start):
 a=s.index(start);i=s.index('{',a);depth=1;j=i+1
 while depth:
  if s[j]=='{':depth+=1
  if s[j]=='}':depth-=1
  j+=1
 return s[a:j]
parts=[extract('private sealed class Record'),extract('private void RestoreMechanicalLinks(')]
out=Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=False)
(out/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><NoWarn>CS0649</NoWarn></PropertyGroup></Project>')
(out/'Subject.cs').write_text('using System.Collections.Concurrent;\npartial class Subject {\n'+'\n'.join(parts)+'\n}')
for name in ['Stubs.cs','Checks.cs']:(out/name).write_bytes((Path(__file__).parent/name).read_bytes())
(out/'boundary.json').write_text(json.dumps({'sourceSha256':hashlib.sha256(s.encode()).hexdigest(),'parts':parts,'boundary':'Exact saved-link owner/closure/identity/native-invocation guards; stock, native links and poses inert. No native game claim.'},indent=2))
subprocess.run(['dotnet','run','--project',str(out/'Probe.csproj'),'-c','Release'],check=True)
