# Builds the Networthy web UI and embeds it into the host.
#
# The app bundle is Networthy's OWN entry (frontend/networthy-ui — the Cortex shell plus the
# custom finance tabs, ADR-0008). It depends on @cortex/ui from the VENDORED tarball in
# .packages/ (put there by scripts/update-platform.ps1), so no Cortex checkout is needed to
# build it. A checkout is only used when present: the dev harness aliases @cortex/ui to its
# source (vite.config.ts), and -WithAdmin rebuilds the admin console from it (normally the
# admin bundle vendors prebuilt from the release instead).
# Outputs are COMMITTED (like .packages/) so a clone runs without pnpm.
# Re-run this script after vendoring a new platform version or editing frontend/networthy-ui.
#
# Usage:  ./scripts/build-ui.ps1 [-WithAdmin] [-CortexRepo <path>]

param(
    [switch]$WithAdmin,
    [string]$CortexRepo = (Join-Path $PSScriptRoot "..\..\Cortex")
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$networthyUi = Join-Path $repoRoot "frontend\networthy-ui"

# Networthy branding + same-origin API base ("" -> relative /api/... calls; the host serves both).
# The vendored @cortex/ui dist was already built with VITE_API_BASE="" by the Cortex release
# workflow; setting it here too keeps the app's own env consistent.
$env:VITE_BRAND_NAME = "Networthy"
$env:VITE_API_BASE = ""
try {
    Write-Host "Installing networthy-ui deps (@cortex/ui from the vendored .packages/ tarball)..." -ForegroundColor Cyan
    pnpm -C $networthyUi install
    if ($LASTEXITCODE -ne 0) { throw "networthy-ui install failed" }

    Write-Host "Building networthy-ui (the branded app: shell + custom finance tabs)..." -ForegroundColor Cyan
    pnpm -C $networthyUi build
    if ($LASTEXITCODE -ne 0) { throw "networthy-ui build failed" }

    if ($WithAdmin) {
        $frontend = Join-Path (Resolve-Path $CortexRepo) "frontend"
        if (-not (Test-Path (Join-Path $frontend "admin-ui\package.json"))) {
            throw "No Cortex frontend at '$frontend' — -WithAdmin needs a checkout (or vendor the prebuilt admin bundle via update-platform.ps1 -WithUi instead)."
        }
        Write-Host "Building @cortex/admin-ui from the checkout (same-origin API)..." -ForegroundColor Cyan
        pnpm -C $frontend install
        if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
        pnpm -C (Join-Path $frontend "admin-ui") build
        if ($LASTEXITCODE -ne 0) { throw "@cortex/admin-ui build failed" }
    }
}
finally {
    Remove-Item Env:VITE_BRAND_NAME -ErrorAction SilentlyContinue
    Remove-Item Env:VITE_API_BASE -ErrorAction SilentlyContinue
}

# Tripwire: a bundle carrying the library's localhost:8080 dev fallback means the vendored
# @cortex/ui dist was built without VITE_API_BASE="" — fail loudly instead of shipping a dead
# app (every API call would leave the host's origin).
$leaked = Get-ChildItem (Join-Path $networthyUi "dist\assets\*.js") |
    Select-String -Pattern "localhost:8080" -List
if ($leaked) {
    throw "networthy-ui bundle contains the localhost:8080 API fallback — the vendored @cortex/ui " +
        "library was built without VITE_API_BASE=`"`"; fix the Cortex release (publish.yml) or re-vendor."
}

$targets = @(
    @{ Source = Join-Path $networthyUi "dist"; Target = Join-Path $repoRoot "src\Networthy.Host\wwwroot\app"; Name = "domain UI (networthy-ui)" }
)
if ($WithAdmin) {
    $targets += @{ Source = Join-Path (Resolve-Path $CortexRepo) "frontend\admin-ui\dist"; Target = Join-Path $repoRoot "src\Networthy.Host\wwwroot\admin"; Name = "admin console" }
}

foreach ($pair in $targets) {
    if (Test-Path $pair.Target) { Remove-Item -Recurse -Force $pair.Target -Confirm:$false }
    New-Item -ItemType Directory -Force (Split-Path $pair.Target) | Out-Null
    Copy-Item -Recurse $pair.Source $pair.Target
    $count = (Get-ChildItem -Recurse -File $pair.Target).Count
    Write-Host "Embedded $($pair.Name): $count file(s) -> $($pair.Target)" -ForegroundColor Green
}

Write-Host "`nDone. Run the host and open it directly - the API serves the Networthy UI at / and /admin." -ForegroundColor Green
