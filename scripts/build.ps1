param([string]$Iscc)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
New-Item build,publish/app,dist,third-party -ItemType Directory -Force | Out-Null
& "$PSScriptRoot/check.ps1"
if ($LASTEXITCODE) { throw 'Checks failed' }
# Build the CPU engine from a pinned official source revision. No private runtime is copied.
if (!(Test-Path build/llama-source/.git)) {
 git clone --depth 1 --branch b11901 https://github.com/ggml-org/llama.cpp.git build/llama-source
 if ($LASTEXITCODE) { throw 'Official engine source download failed' }
}
$revision = (git -C build/llama-source rev-parse HEAD).Trim()
if (!$revision.StartsWith('5320c87')) { throw "Unexpected official engine revision: $revision" }
cmake -S build/llama-source -B build/llama-build -A x64 -DBUILD_SHARED_LIBS=OFF -DGGML_NATIVE=OFF -DGGML_OPENMP=OFF -DLLAMA_CURL=OFF -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_SERVER=ON -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE) { throw 'Engine configuration failed' }
cmake --build build/llama-build --config Release --target llama-server -j 4
if ($LASTEXITCODE) { throw 'Engine compilation failed' }
$runtime = Join-Path $root 'build/engine'
New-Item $runtime -ItemType Directory -Force | Out-Null
Copy-Item build/llama-build/bin/Release/llama-server.exe $runtime
$notices = Join-Path $root 'publish/app/third-party/llama.cpp'
New-Item $notices -ItemType Directory -Force | Out-Null
Copy-Item build/llama-source/LICENSE $notices
Get-ChildItem build/llama-source -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)(\.|-|$)' -and $_.FullName -notmatch '\\.git\\' } | ForEach-Object {
 $relative = [IO.Path]::GetRelativePath((Join-Path $root 'build/llama-source'),$_.FullName)
 $target = Join-Path $notices $relative
 New-Item (Split-Path $target) -ItemType Directory -Force | Out-Null
 Copy-Item -LiteralPath $_.FullName -Destination $target
}
$files = @(Get-ChildItem $runtime -File | ForEach-Object { @{path=$_.Name;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
$manifest = @{schemaVersion=1;source='https://github.com/ggml-org/llama.cpp';revision=$revision;backend='cpu';files=$files}
$manifest | ConvertTo-Json -Depth 6 | Set-Content "$runtime/engine-manifest.json" -Encoding utf8NoBOM
$trust = (Get-FileHash "$runtime/engine-manifest.json").Hash.ToLowerInvariant()
'[assembly: System.Reflection.AssemblyMetadata("EngineManifestSha256","'+$trust+'")]' | Set-Content build/EngineTrust.g.cs -Encoding utf8NoBOM
dotnet publish src/SS.App/SS.App.csproj -c Release -o publish/app
if ($LASTEXITCODE) { throw 'App publish failed' }
dotnet publish src/SS.Power/SS.Power.csproj -c Release -o publish/power
if ($LASTEXITCODE) { throw 'Power helper publish failed' }
Copy-Item publish/power/* publish/app -Force -Recurse
New-Item publish/app/engines/runtime -ItemType Directory -Force | Out-Null
Copy-Item "$runtime/*" publish/app/engines/runtime
Copy-Item README.md publish/app/README.md
if (!$Iscc) { $Iscc = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6/ISCC.exe' }
if (!(Test-Path $Iscc)) { throw 'Inno Setup compiler missing' }
& $Iscc installer/core.iss
if ($LASTEXITCODE) { throw 'Installer core failed' }
$hash = (Get-FileHash build/SS.CoreSetup.exe).Hash.ToLowerInvariant()
'namespace SSSetup { internal static class ReleasePayload { public const string Sha256="'+$hash+'"; } }' | Set-Content src/SS.Setup/ReleasePayload.g.cs -Encoding utf8NoBOM
dotnet publish src/SS.Setup/SS.Setup.csproj -c Release -o publish/setup
if ($LASTEXITCODE) { throw 'Setup front end publish failed' }
Copy-Item publish/setup/SSLocalModelManager-Setup.exe dist/SSLocalModelManager-4.0.0-beta.1-win-x64-Setup.exe
& "$PSScriptRoot/smoke.ps1"
if ($LASTEXITCODE) { throw 'Installed application smoke failed' }
Get-ChildItem dist/*.exe | ForEach-Object { ((Get-FileHash $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name) } | Set-Content dist/SHA256SUMS.txt -Encoding ascii
