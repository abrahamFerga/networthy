# Builds the Networthy web UI and embeds it into the host.
#
# The app bundle is Networthy's OWN entry (frontend/networthy-ui — the Plenipo shell plus the
# custom finance tabs, ADR-0008). It depends on @plenipo/ui from the public npm registry (pinned
# by scripts/update-platform.ps1), so no Plenipo checkout is needed to build it. A checkout is
# only used when present: the dev harness aliases @plenipo/ui to its source (vite.config.ts),
# and -WithAdmin rebuilds the admin console from it (normally the admin bundle vendors prebuilt
# from the release instead).
# Outputs are COMMITTED (like .packages/) so a clone runs without pnpm.
# Re-run this script after vendoring a new platform version or editing frontend/networthy-ui.
#
# Usage:  ./scripts/build-ui.ps1 [-WithAdmin] [-PlenipoRepo <path>]

param(
    [switch]$WithAdmin,
    [string]$PlenipoRepo = (Join-Path $PSScriptRoot "..\..\Plenipo")
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$networthyUi = Join-Path $repoRoot "frontend\networthy-ui"

# Networthy branding + same-origin API base ("" -> relative /api/... calls; the host serves both).
# The published @plenipo/ui dist was already built with VITE_API_BASE="" by the Plenipo release
# workflow; setting it here too keeps the app's own env consistent.
$env:VITE_BRAND_NAME = "Networthy"
$env:VITE_API_BASE = ""
try {
    Write-Host "Installing networthy-ui deps (@plenipo/ui from the npm registry)..." -ForegroundColor Cyan
    pnpm -C $networthyUi install
    if ($LASTEXITCODE -ne 0) { throw "networthy-ui install failed" }

    Write-Host "Building networthy-ui (the branded app: shell + custom finance tabs)..." -ForegroundColor Cyan
    pnpm -C $networthyUi build
    if ($LASTEXITCODE -ne 0) { throw "networthy-ui build failed" }

    if ($WithAdmin) {
        # Test-Path BEFORE Resolve-Path: with $ErrorActionPreference = "Stop", resolving a missing
        # path throws a raw "Cannot find path" before the friendly guard below can explain itself.
        if (-not (Test-Path $PlenipoRepo)) {
            throw "No Plenipo checkout at '$PlenipoRepo' — -WithAdmin needs one (pass -PlenipoRepo <path>, or vendor the prebuilt admin bundle via update-platform.ps1 -WithUi instead)."
        }
        $frontend = Join-Path (Resolve-Path $PlenipoRepo) "frontend"
        if (-not (Test-Path (Join-Path $frontend "admin-ui\package.json"))) {
            throw "No Plenipo frontend at '$frontend' — -WithAdmin needs a checkout (or vendor the prebuilt admin bundle via update-platform.ps1 -WithUi instead)."
        }
        Write-Host "Building @plenipo/admin-ui from the checkout (same-origin API)..." -ForegroundColor Cyan
        pnpm -C $frontend install
        if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
        pnpm -C (Join-Path $frontend "admin-ui") build
        if ($LASTEXITCODE -ne 0) { throw "@plenipo/admin-ui build failed" }
    }
}
finally {
    Remove-Item Env:VITE_BRAND_NAME -ErrorAction SilentlyContinue
    Remove-Item Env:VITE_API_BASE -ErrorAction SilentlyContinue
}

# Tripwire: the published @plenipo/ui dist bakes its API base in at LIBRARY build time, and for
# this repo it must be the empty, same-origin one — Networthy.Host serves the app and the API from
# one origin, so any absolute base would send every call off the host and leave a dead app.
#
# Asserted on the configuration the bundle actually performs, not on the presence of the string
# "localhost:8080". That string match was the check until @plenipo/ui 0.1.0-alpha.29, which split
# the client into @plenipo/client; the fallback now lives in THAT package's prebuilt dist as
# `normalizeApiBase(raw) => (raw ?? "http://localhost:8080")`, so the literal ships in every
# correct bundle as an unreachable default and the old rule failed a build that was fine. The two
# checks below are strictly stronger: the first fails if the bundle never configures a same-origin
# base at all (which the string match could not see), the second if it configures an absolute one
# — the actual failure the tripwire was written for.
$assets = Get-ChildItem (Join-Path $networthyUi "dist\assets\*.js")
if (-not ($assets | Select-String -Pattern 'baseUrl:\s*""' -List)) {
    throw "networthy-ui bundle never configures a same-origin API base — the published @plenipo/ui " +
        "library was built without VITE_API_BASE=`"`"; fix the Plenipo release (publish.yml) or re-pin."
}
$absolute = $assets | Select-String -Pattern 'baseUrl:\s*"https?:' -List
if ($absolute) {
    throw "networthy-ui bundle configures an ABSOLUTE API base ($($absolute.Matches[0].Value)) — every " +
        "API call would leave the host's origin. Rebuild @plenipo/ui with VITE_API_BASE=`"`"."
}

$targets = @(
    @{ Source = Join-Path $networthyUi "dist"; Target = Join-Path $repoRoot "src\Networthy.Host\wwwroot\app"; Name = "domain UI (networthy-ui)" }
)
if ($WithAdmin) {
    $targets += @{ Source = Join-Path (Resolve-Path $PlenipoRepo) "frontend\admin-ui\dist"; Target = Join-Path $repoRoot "src\Networthy.Host\wwwroot\admin"; Name = "admin console" }
}

foreach ($pair in $targets) {
    if (Test-Path $pair.Target) { Remove-Item -Recurse -Force $pair.Target -Confirm:$false }
    New-Item -ItemType Directory -Force (Split-Path $pair.Target) | Out-Null
    Copy-Item -Recurse $pair.Source $pair.Target
    $count = (Get-ChildItem -Recurse -File $pair.Target).Count
    Write-Host "Embedded $($pair.Name): $count file(s) -> $($pair.Target)" -ForegroundColor Green
}

Write-Host "`nDone. Run the host and open it directly - the API serves the Networthy UI at / and /admin." -ForegroundColor Green
