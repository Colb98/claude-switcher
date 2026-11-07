# Builds dist\ClaudeSwitcher.exe: self-contained, single file, no .NET install needed on the target PC.
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
# A dotnet on PATH may be a runtime-only install; fall back to the per-user SDK then.
$sdks = $null
if ($dotnet) { try { $sdks = & $dotnet --list-sdks 2>$null } catch { } }
if (-not $sdks) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1
$root = Split-Path $PSScriptRoot -Parent
& $dotnet publish "$root\src\ClaudeSwitcher\ClaudeSwitcher.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
