$ErrorActionPreference='Stop'
trap { Get-ChildItem (Join-Path $env:SS_MODEL_DATA_ROOT 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Tail 120 }; break }
$root=Split-Path $PSScriptRoot -Parent
$test=Join-Path $env:RUNNER_TEMP ('ss-smoke-'+[Guid]::NewGuid().ToString('N'))
New-Item $test -ItemType Directory | Out-Null
$target=Join-Path $test 'installed'
$env:SS_MODEL_DATA_ROOT=Join-Path $test 'data'
$setup=Join-Path $root 'dist/SSLocalModelManager-4.0.0-beta.1-win-x64-Setup.exe'
$p=Start-Process -FilePath $setup -ArgumentList @('--unattended','--accept-license','--no-icons','--dir',('"{0}"' -f $target),'--report',('"{0}"' -f (Join-Path $test 'install.json'))) -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode) { throw "Unattended installation failed: $($p.ExitCode)" }
$app=Join-Path $target 'SSLocalModelManager.exe'
if (!(Test-Path $app)) { throw 'Installed entry missing' }
function App([string[]]$Arguments) {
 $p=Start-Process -FilePath $app -ArgumentList $Arguments -PassThru -WindowStyle Hidden
 if (!$p.WaitForExit(120000)) { throw "Application control command timed out: $Arguments" }
 if ($p.ExitCode) { Get-ChildItem $env:SS_MODEL_DATA_ROOT -Recurse -Filter "*fatal*" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }; Get-WinEvent -FilterHashtable @{LogName="Application";StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Where-Object { $_.Id -in @(1000,1026) } | Select-Object -First 4 -ExpandProperty Message; throw "Application failed: $Arguments, exit $($p.ExitCode)" }
}
App @('--selftest')
# Test weights are downloaded only on the disposable GitHub runner; never shipped.
$model=Join-Path $test 'Qwen3-0.6B-Q8_0.gguf'
Invoke-WebRequest 'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q8_0.gguf' -OutFile $model -TimeoutSec 180
if ((Get-FileHash $model).Hash.ToLowerInvariant() -ne '9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031') { throw 'Official test model checksum mismatch' }
App @('--import',('"{0}"' -f $model))
$registry=Get-Content (Join-Path $env:SS_MODEL_DATA_ROOT 'config/models.json') -Raw | ConvertFrom-Json -AsHashtable
$id=@($registry.models.Keys)[0]
App @('--service','start',$id)
$base='http://127.0.0.1:17881'
$ready=$false
for ($i=0;$i -lt 120;$i++) {
 try { $health=Invoke-RestMethod "$base/health" -TimeoutSec 3; if ($health.status -eq 'ok') { $ready=$true;break } } catch {}
 Start-Sleep -Seconds 1
}
if (!$ready) { throw 'Installed engine never became healthy' }
$body=@{model=$id;messages=@(@{role='user';content='Reply in English with one word: hello. /no_think'});max_tokens=32;temperature=0;stream=$false} | ConvertTo-Json -Depth 6
$reply=Invoke-RestMethod "$base/v1/chat/completions" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 180
if (!$reply.choices -or $reply.usage.completion_tokens -lt 1) { throw 'Real inference returned no tokens' }
$session=Get-Content (Join-Path $env:SS_MODEL_DATA_ROOT 'logs/llama-session.json') -Raw | ConvertFrom-Json
if ($session.alias -ne $id -or !(Test-Path $session.log)) { throw 'KV session is not tied to this model' }
$screen=Join-Path $root 'dist/screenshots'
New-Item $screen -ItemType Directory -Force | Out-Null
App @('--preview',('"{0}"' -f (Join-Path $screen 'dashboard-dark.png')),'0','dark','zh','1120','680')
App @('--preview',('"{0}"' -f (Join-Path $screen 'monitor-light.png')),'2','light','en','1120','680')
App @('--telemetry',('"{0}"' -f (Join-Path $test 'telemetry.json')))
$telemetry=Get-Content (Join-Path $test 'telemetry.json') -Raw | ConvertFrom-Json
if (!$telemetry.Online -or $telemetry.Model -ne $id) { throw 'Monitor did not identify installed engine' }
if ($null -eq $telemetry.KvCpuMiB -and $null -eq $telemetry.KvHostGiB) { throw 'Real KV allocation is missing' }
App @('--service','stop')
# Immediate restart must wait for old gateway and lease cleanup.
App @('--service','start',$id)
$ready=$false
for ($i=0;$i -lt 120;$i++) {
 try { $health=Invoke-RestMethod "$base/health" -TimeoutSec 3; if ($health.status -eq 'ok') { $ready=$true;break } } catch {}
 Start-Sleep -Seconds 1
}
if (!$ready) { throw 'Immediate restart failed' }
$newSession=Get-Content (Join-Path $env:SS_MODEL_DATA_ROOT 'logs/llama-session.json') -Raw | ConvertFrom-Json
$oldLease=Get-Content (Join-Path $env:SS_MODEL_DATA_ROOT 'transactions/native-host.json') -Raw | ConvertFrom-Json
App @('--telemetry',('"{0}"' -f (Join-Path $test 'telemetry-restarted.json')))
$restarted=Get-Content (Join-Path $test 'telemetry-restarted.json') -Raw | ConvertFrom-Json
if (!$restarted.Online -or $restarted.KvCpuMiB -ne $telemetry.KvCpuMiB) { throw 'Restarted KV was missing or mixed with old allocation' }
App @('--service','stop')
$uninstaller=Join-Path $target '_uninstall/unins000.exe'
$p=Start-Process $uninstaller -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode) { throw 'Uninstall failed' }
if (!(Test-Path $model) -or !(Test-Path (Join-Path $env:SS_MODEL_DATA_ROOT 'config/models.json'))) { throw 'Uninstall removed external model or user configuration' }
@{schemaVersion=1;version='4.0.0-beta.1';unattendedInstall=$true;realCpuInference=$true;kvAllocation=$true;uninstallPreservesModel=$true;model='Qwen3-0.6B-Q8_0';testedUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $root 'dist/verification.json') -Encoding utf8NoBOM
