# Cross-compiles the Go relay server for VPS deployment.
# Requires the Go toolchain (https://go.dev/dl/) on PATH or at the default
# location this repo's tooling uses.

$ErrorActionPreference = "Stop"

$go = "go"
if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    $fallback = "$env:LOCALAPPDATA\go-toolchain\go\bin\go.exe"
    if (Test-Path $fallback) { $go = $fallback }
    else { throw "Go toolchain not found. Install from https://go.dev/dl/" }
}

$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root "server"
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

Push-Location $src
try {
    Write-Host "==> go vet"
    & $go vet ./...
    if ($LASTEXITCODE -ne 0) { throw "go vet failed" }

    Write-Host "==> go test"
    & $go test -count=1 ./...
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }

    $targets = @(
        @{ OS = "linux";   Arch = "amd64"; Out = "gamerelay-server-linux-amd64" },
        @{ OS = "linux";   Arch = "arm64"; Out = "gamerelay-server-linux-arm64" },
        @{ OS = "windows"; Arch = "amd64"; Out = "gamerelay-server-windows-amd64.exe" }
    )
    foreach ($t in $targets) {
        Write-Host "==> building $($t.Out)"
        $env:GOOS = $t.OS
        $env:GOARCH = $t.Arch
        $env:CGO_ENABLED = "0"
        & $go build -trimpath -ldflags "-s -w" -o (Join-Path $dist $t.Out) .
        if ($LASTEXITCODE -ne 0) { throw "build failed for $($t.Out)" }
    }
}
finally {
    Pop-Location
    Remove-Item Env:GOOS, Env:GOARCH -ErrorAction SilentlyContinue
}

Write-Host "==> done. binaries in $dist"
Get-ChildItem $dist | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
