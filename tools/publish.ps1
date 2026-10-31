# Builds dist\ClaudeSwitcher.exe: self-contained, single file, no .NET install needed on the target PC.
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1
$root = Split-Path $PSScriptRoot -Parent
& $dotnet publish "$root\src\ClaudeSwitcher\ClaudeSwitcher.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
