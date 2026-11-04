from pathlib import Path
import hashlib,json,subprocess,sys
root=Path(__file__).resolve().parents[2]
source=root/'ServerPlugin/Accounting/AccountingEngine.Sun.cs'
s=source.read_text();start=s.index('internal static bool SolarWorkerStep(');end=s.index('\n\t/// <summary>',start)
out=Path(sys.argv[1]);out.mkdir(parents=True,exist_ok=False)
(out/'Check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion></PropertyGroup></Project>')
(out/'Subject.cs').write_text('using System.Threading; namespace Concealment.Accounting; static class AccountingEngine {\n'+s[start:end]+'\n}')
for name in ['Checks.cs','Scheduler.cs']:(out/name).write_bytes((Path(__file__).parent/name).read_bytes())
(out/'inputs.json').write_text(json.dumps({'source':str(source),'sha256':hashlib.sha256(source.read_bytes()).hexdigest(),'boundary':'Exact dispatch helper, inert native-scheduler stand-in. Proves dispatch/join/error invariants, NOT physics safety or timing.'},indent=2)+'\n')
subprocess.run(['dotnet','run','--project',str(out/'Check.csproj'),'-c','Release'],check=True)
