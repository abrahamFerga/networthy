# Builds the Networthy web UI and embeds it into the host.
#
# The app bundle is Networthy's OWN entry (frontend/networthy-ui — the Cortex shell plus the
# custom finance Overview dashboard, ADR-0008), which depends on @cortex/ui from a Cortex
# checkout (file: dependency); the admin console still builds straight from that checkout.
# Everything is served by the API host itself — same origin, no CORS, no registry — and the
# outputs are COMMITTED (like .packages/) so a clone runs without a Cortex checkout or pnpm.
# Re-run this script after pulling Cortex frontend changes or editing frontend/networthy-ui.
#
# Usage:  ./scripts/build-ui.ps1 [-CortexRepo <path>]   (default: the sibling ../Cortex checkout)

param(
    [string]$CortexRepo = (Join-Path $PSScriptRoot "..\..\Cortex")
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$CortexRepo = Resolve-Path $CortexRepo
$frontend = Join-Path $CortexRepo "frontend"
$networthyUi = Join-Path $repoRoot "frontend\networthy-ui"

if (-not (Test-Path (Join-Path $frontend "cortex-ui\package.json"))) {
    throw "No Cortex frontend at '$frontend' — pass -CortexRepo pointing at a Cortex checkout."
}

Write-Host "Installing Cortex frontend workspace deps..." -ForegroundColor Cyan
pnpm -C $frontend install
if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }

# networthy-ui consumes @cortex/ui's LIBRARY build (dist/), so build that first.
Write-Host "Building the @cortex/ui library..." -ForegroundColor Cyan
pnpm -C (Join-Path $frontend "cortex-ui") build
if ($LASTEXITCODE -ne 0) { throw "@cortex/ui library build failed" }

Write-Host "Installing networthy-ui deps (links @cortex/ui from the checkout)..." -ForegroundColor Cyan
pnpm -C $networthyUi install
if ($LASTEXITCODE -ne 0) { throw "networthy-ui install failed" }

# Networthy branding + same-origin API base ("" -> relative /api/... calls; the host serves both).
$env:VITE_BRAND_NAME = "Networthy"
$env:VITE_API_BASE = ""
try {
    Write-Host "Building networthy-ui (the branded app: shell + Overview dashboard)..." -ForegroundColor Cyan
    pnpm -C $networthyUi build
    if ($LASTEXITCODE -ne 0) { throw "networthy-ui build failed" }

    Write-Host "Building @cortex/admin-ui (same-origin API)..." -ForegroundColor Cyan
    pnpm -C (Join-Path $frontend "admin-ui") build
    if ($LASTEXITCODE -ne 0) { throw "@cortex/admin-ui build failed" }
}
finally {
    Remove-Item Env:VITE_BRAND_NAME -ErrorAction SilentlyContinue
    Remove-Item Env:VITE_API_BASE -ErrorAction SilentlyContinue
}

$appTarget = Join-Path $repoRoot "src\Networthy.Host\wwwroot\app"
$adminTarget = Join-Path $repoRoot "src\Networthy.Host\wwwroot\admin"

foreach ($pair in @(
    @{ Source = Join-Path $networthyUi "dist"; Target = $appTarget; Name = "domain UI (networthy-ui)" },
    @{ Source = Join-Path $frontend "admin-ui\dist"; Target = $adminTarget; Name = "admin console" }
)) {
    if (Test-Path $pair.Target) { Remove-Item -Recurse -Force $pair.Target }
    New-Item -ItemType Directory -Force (Split-Path $pair.Target) | Out-Null
    Copy-Item -Recurse $pair.Source $pair.Target
    $count = (Get-ChildItem -Recurse -File $pair.Target).Count
    Write-Host "Embedded $($pair.Name): $count file(s) -> $($pair.Target)" -ForegroundColor Green
}

Write-Host "`nDone. Run the host and open it directly - the API now serves the Networthy UI at / and /admin." -ForegroundColor Green
