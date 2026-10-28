$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$test=Join-Path $env:RUNNER_TEMP ('ss-smoke-'+[Guid]::NewGuid().ToString('N'))
New-Item $test -ItemType Directory | Out-Null
$target=Join-Path $test 'installed'
$env:SS_MODEL_DATA_ROOT=Join-Path $test 'data'
trap { Get-ChildItem (Join-Path $env:SS_MODEL_DATA_ROOT 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Tail 100 }; break }
$setup=Join-Path $root 'dist/SSLocalModelManager-4.0.0-beta.1-win-x64-Setup.exe'
$p=Start-Process -FilePath $setup -ArgumentList @('--unattended','--accept-license','--no-icons','--dir',('"{0}"' -f $target),'--report',('"{0}"' -f (Join-Path $test 'install.json'))) -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode) { throw "Unattended installation failed: $($p.ExitCode)" }
$app=Join-Path $target 'SSLocalModelManager.exe'
if (!(Test-Path $app)) { throw 'Installed entry missing' }
$entryVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($app)
if ($entryVersion.FileVersion -ne '4.0.0.0' -or !$entryVersion.ProductVersion.StartsWith('4.0.0-beta.1')) { throw 'Installed version mismatch' }
function Control([string[]]$Arguments) {
 $p=Start-Process -FilePath $app -ArgumentList $Arguments -PassThru -WindowStyle Hidden
 if (!$p.WaitForExit(120000)) { throw "Control command timed out: $Arguments" }
 return $p.ExitCode
}
function App([string[]]$Arguments) {
 $code=Control $Arguments
 if ($code) { throw "Application failed: $Arguments, exit $code" }
}
$base='http://127.0.0.1:17881'
function WaitReady([string]$Alias) {
 for ($i=0;$i -lt 90;$i++) {
  try {
   $health=Invoke-RestMethod "$base/health" -NoProxy -TimeoutSec 3
   $served=Invoke-RestMethod "$base/v1/models" -NoProxy -TimeoutSec 3
   if ($health.status -eq 'ok' -and $served.data[0].id -eq $Alias) { return }
  } catch {}
  Start-Sleep -Milliseconds 500
 }
 Get-Content (Join-Path $env:SS_MODEL_DATA_ROOT 'transactions/native-host.json') -ErrorAction SilentlyContinue
 try { $r=Invoke-WebRequest "$base/health" -NoProxy -SkipHttpErrorCheck -TimeoutSec 5; Write-Host $r.StatusCode $r.Content } catch { Write-Host $_ }
 throw "Expected model did not become healthy: $Alias"
}
function Lease { Get-Content (Join-Path $env:SS_MODEL_DATA_ROOT 'transactions/native-host.json') -Raw | ConvertFrom-Json }
App @('--selftest')
Write-Host 'PASS installed entry and product version'
# Test weights exist only on the disposable runner, and are never shipped.
$model=Join-Path $test 'Qwen3-0.6B-Q8_0.gguf'
Invoke-WebRequest 'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q8_0.gguf' -OutFile $model -TimeoutSec 180
$modelHash=(Get-FileHash $model).Hash.ToLowerInvariant()
if ($modelHash -ne '9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031') { throw 'Official test model checksum mismatch' }
App @('--import',('"{0}"' -f $model))
$registryPath=Join-Path $env:SS_MODEL_DATA_ROOT 'config/models.json'
$registry=Get-Content $registryPath -Raw | ConvertFrom-Json -AsHashtable
$id=@($registry.models.Keys)[0]
# Two aliases of one immutable fixture exercise the real single-engine switch.
$alternate='ss-test-alternate'
$second=@{}
foreach ($key in $registry.models[$id].Keys) { $second[$key]=$registry.models[$id][$key] }
$second.id=$alternate
$second.alias=$alternate
$registry.models[$alternate]=$second
$registry | ConvertTo-Json -Depth 12 | Set-Content $registryPath -Encoding utf8NoBOM
App @('--service','start',$id)
WaitReady $id
$firstLease=Lease
$body=@{model=$id;messages=@(@{role='user';content='Reply with one English word: hello. /no_think'});max_tokens=32;temperature=0;stream=$false} | ConvertTo-Json -Depth 6
$reply=Invoke-RestMethod "$base/v1/chat/completions" -NoProxy -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 180
if (!$reply.choices -or $reply.usage.completion_tokens -lt 1) { throw 'Real inference returned no tokens' }
Write-Host 'PASS real CPU chat completion'
$shortStream=@{model=$id;messages=@(@{role='user';content='Say hello. /no_think'});max_tokens=16;stream=$true} | ConvertTo-Json -Depth 6
$sse=Invoke-WebRequest "$base/v1/chat/completions" -NoProxy -Method Post -ContentType 'application/json' -Body $shortStream -TimeoutSec 180
if (!$sse.Content.Contains('data: [DONE]')) { throw 'Real SSE did not complete' }
Write-Host 'PASS real streaming completion'
App @('--telemetry',('"{0}"' -f (Join-Path $test 'telemetry.json')))
$telemetry=Get-Content (Join-Path $test 'telemetry.json') -Raw | ConvertFrom-Json
if (!$telemetry.Online -or $telemetry.Model -ne $id) { throw 'Monitor identified the wrong engine' }
if ($null -eq $telemetry.KvCpuMiB -or $telemetry.KvCpuMiB -le 0) { throw 'Real KV allocation is missing' }
Write-Host "PASS real CPU KV allocation: $($telemetry.KvCpuMiB) MiB"
$screen=Join-Path $root 'dist/screenshots'
New-Item $screen -ItemType Directory -Force | Out-Null
foreach ($page in 0..4) {
 $name=if ($page -eq 0) {'dashboard-dark.png'} else {"page-$page-dark.png"}
 App @('--preview',('"{0}"' -f (Join-Path $screen $name)),$page.ToString(),'dark','zh','1120','680')
}
foreach ($section in 0..3) {
 App @('--preview',('"{0}"' -f (Join-Path $screen "monitor-$section-light.png")),'2','light','en','1120','680',$section.ToString())
}
foreach ($image in Get-ChildItem $screen -Filter '*.png') { if ($image.Length -lt 1000) { throw 'Empty screenshot' } }
if (@(Get-ChildItem $screen -Filter '*.png').Count -ne 9) { throw 'A page failed to render' }
Write-Host 'PASS five pages and four monitor sections'
$invalid=Control @('--service','start','ss-missing-fixture')
if (!$invalid -or (Lease).sessionId -ne $firstLease.sessionId) { throw 'Invalid target interrupted the running model' }
Write-Host 'PASS invalid target preserves running model'
$handler=[Net.Http.HttpClientHandler]::new()
$handler.UseProxy=$false
$client=[Net.Http.HttpClient]::new($handler)
$response=$null
try {
 $busyBody=@{model=$id;messages=@(@{role='user';content='Count in order from 1 to 1000, separating each number with a space. /no_think'});max_tokens=512;ignore_eos=$true;stream=$true} | ConvertTo-Json -Depth 6
 $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"$base/v1/chat/completions")
 $request.Content=[Net.Http.StringContent]::new($busyBody,[Text.Encoding]::UTF8,'application/json')
 $response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
 if (!$response.IsSuccessStatusCode) { throw 'Busy fixture request rejected' }
 $reader=[IO.StreamReader]::new($response.Content.ReadAsStream())
 if (!$reader.ReadLine().StartsWith('data:')) { throw 'Busy stream did not start' }
 $rejected=Control @('--service','start',$alternate)
 if (!$rejected -or (Lease).sessionId -ne $firstLease.sessionId) { throw 'Busy switch did not preserve the running model' }
 Write-Host 'PASS busy switch refused and current engine preserved'
} finally {
 if ($response) { $response.Dispose() }
 $client.Dispose()
}
$idle=$false
for ($i=0;$i -lt 100;$i++) {
 $slots=Invoke-RestMethod "$base/slots" -NoProxy -TimeoutSec 3
 if (@($slots).Count -gt 0 -and @($slots | Where-Object is_processing).Count -eq 0) { $idle=$true;break }
 Start-Sleep -Milliseconds 200
}
if (!$idle) { throw 'Canceled stream did not return to idle' }
App @('--service','start',$alternate)
WaitReady $alternate
if ((Lease).sessionId -eq $firstLease.sessionId) { throw 'Model switch did not create a fresh session' }
Write-Host 'PASS idle model switch'
$beforeRestart=Lease
App @('--service','restart',$alternate)
WaitReady $alternate
if ((Lease).sessionId -eq $beforeRestart.sessionId) { throw 'Parameter restart reused the old session' }
App @('--telemetry',('"{0}"' -f (Join-Path $test 'telemetry-restarted.json')))
$restarted=Get-Content (Join-Path $test 'telemetry-restarted.json') -Raw | ConvertFrom-Json
if (!$restarted.Online -or $restarted.KvCpuMiB -ne $telemetry.KvCpuMiB) { throw 'Restarted KV was missing or mixed with old allocation' }
Write-Host 'PASS restart and fresh KV session'
App @('--service','stop')
App @('--service','start',$id)
WaitReady $id
App @('--service','stop')
Write-Host 'PASS immediate stop/start'
$uninstaller=Join-Path $target '_uninstall/unins000.exe'
$p=Start-Process $uninstaller -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode) { throw 'Uninstall failed' }
if (!(Test-Path $model) -or !(Test-Path $registryPath) -or (Get-FileHash $model).Hash.ToLowerInvariant() -ne $modelHash) { throw 'Uninstall changed external model or removed user configuration' }
Write-Host 'PASS uninstall preserves external model and user registry'
@{schemaVersion=1;version='4.0.0-beta.1';sourceCommit=$env:GITHUB_SHA;appSha256=(Get-FileHash (Join-Path $root 'publish/app/SSLocalModelManager.exe')).Hash.ToLowerInvariant();installerSha256=(Get-FileHash $setup).Hash.ToLowerInvariant();unattendedInstall=$true;realCpuInference=$true;realStreaming=$true;kvAllocationMiB=$telemetry.KvCpuMiB;pageRenders=9;busySwitchPreservesEngine=$true;idleSwitch=$true;restart=$true;uninstallPreservesModel=$true;gpuAndPower='not_tested';model='Qwen3-0.6B-Q8_0';testedUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $root 'dist/verification.json') -Encoding utf8NoBOM
