$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
 dotnet build tests/SS.Checks.csproj -c Release
 if ($LASTEXITCODE) { throw 'Checks failed to compile' }
 $checks = Join-Path $root 'tests/bin/Release/net10.0-windows/SS.Checks.dll'
 Push-Location data/tuning
 try {
  foreach ($test in @('PathChecks','DiscoveryChecks','NativeHostTests','RecoveryChecks','GatewayIntegrationTests','AdviceChecks','SetupProtocolChecks')) {
   dotnet $checks $test
   if ($LASTEXITCODE) { throw "$test failed" }
  }
 } finally { Pop-Location }
} finally { Pop-Location }
