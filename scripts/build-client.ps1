# Publishes the Windows client as a self-contained x64 app (no .NET or
# Windows App SDK installation required on the target machine).

$ErrorActionPreference = "Stop"

$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $fallback = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    if (Test-Path $fallback) { $dotnet = $fallback }
    else { throw ".NET SDK not found. Install .NET 9 SDK." }
}

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "client\GameRelay.App\GameRelay.App.csproj"
$out = Join-Path $root "dist\GameRelay-win-x64"

# Stage the relay-server binaries + installer the app uploads via its SSH setup
# wizard. These are produced by build-server.ps1 (run it first).
$assets = Join-Path $root "client\GameRelay.App\ServerAssets"
$dist = Join-Path $root "dist"
$deploy = Join-Path $root "server\deploy"
$needed = @("gamerelay-server-linux-amd64", "gamerelay-server-linux-arm64")
if ($needed | Where-Object { -not (Test-Path (Join-Path $dist $_)) }) {
    throw "server binaries missing in dist\ — run scripts\build-server.ps1 first"
}
New-Item -ItemType Directory -Force $assets | Out-Null
foreach ($f in $needed) { Copy-Item (Join-Path $dist $f) $assets -Force }
Copy-Item (Join-Path $deploy "install.sh") $assets -Force
Copy-Item (Join-Path $deploy "gamerelay.service") $assets -Force
Write-Host "==> staged server assets into ServerAssets\"

Write-Host "==> publishing GameRelay (self-contained win-x64)"
& $dotnet publish $proj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "==> done: $out\GameRelay.exe"
