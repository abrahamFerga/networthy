# Updates the vendored Cortex platform from a GitHub Release — no Cortex checkout, no pnpm,
# no registry auth (release assets download anonymously).
#
#   ./scripts/update-platform.ps1 -Version 0.1.0-alpha.14          # nupkgs into .packages/
#   ./scripts/update-platform.ps1 -Version 0.1.0-alpha.14 -WithUi  # + brand-agnostic UI bundles
#
# What it does:
#  1. Downloads every Cortex.*.nupkg from the release into .packages/ (removing the old version)
#     and bumps the Version="…" references in src/**.csproj.
#  2. Downloads the @cortex/ui library tarball (cortex-ui-<version>.tgz) into .packages/ and
#     points frontend/networthy-ui's dependency at it — the app bundle then rebuilds from the
#     vendored library via scripts/build-ui.ps1, no Cortex checkout needed (ADR-0008).
#  3. With -WithUi: downloads cortex-admin-ui.zip into src/Networthy.Host/wwwroot/admin (the
#     admin console stays prebuilt; the APP bundle is always Networthy's own build).
#  4. Prints the follow-ups: rebuild UI, restore, test, update .gitignore's pin, commit.
#
# Both .packages/ and wwwroot/ stay COMMITTED — a bare clone remains the whole product.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$WithUi,
    [string]$Repo = "abrahamFerga/Cortex"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$tag = "v$Version"
$base = "https://github.com/$Repo/releases/download/$tag"

$packages = @(
    "Cortex.Core", "Cortex.Application", "Cortex.Infrastructure", "Cortex.AspNetCore",
    "Cortex.ServiceDefaults", "Cortex.Modules.Sdk", "Cortex.Connectors.Sdk",
    "Cortex.Connectors", "Cortex.Cli"
)

Write-Host "Fetching Cortex $Version from $Repo release $tag..." -ForegroundColor Cyan
$feed = Join-Path $repoRoot ".packages"
# Remove other versions FIRST. Re-vendoring the SAME version (e.g. switching provenance from a
# local pack to the official release) reuses identical filenames — deleting "old" files after
# the download would delete the downloads themselves.
$old = Get-ChildItem $feed -Filter "Cortex.*.nupkg" | Where-Object { $_.Name -notlike "*.$Version.nupkg" }
$old | Remove-Item -Force -Confirm:$false
Write-Host "  - removed $($old.Count) other-version nupkg(s)"
foreach ($p in $packages) {
    $file = "$p.$Version.nupkg"
    Invoke-WebRequest "$base/$file" -OutFile (Join-Path $feed $file) -UseBasicParsing
    Write-Host "  + $file"
}

# The @cortex/ui LIBRARY tarball — frontend/networthy-ui's build input (ADR-0008), vendored like
# the nupkgs so building the app bundle needs no Cortex checkout. The dev harness still prefers a
# checkout's source when one sits next to this repo (vite.config.ts aliases it for live env/HMR).
$oldTgz = Get-ChildItem $feed -Filter "cortex-ui-*.tgz" | Where-Object { $_.Name -ne "cortex-ui-$Version.tgz" }
$oldTgz | Remove-Item -Force -Confirm:$false
$tgz = "cortex-ui-$Version.tgz"
Invoke-WebRequest "$base/$tgz" -OutFile (Join-Path $feed $tgz) -UseBasicParsing
Write-Host "  + $tgz"

# Point networthy-ui's dependency at the vendored tarball (the path encodes the version).
$pkgJsonPath = Join-Path $repoRoot "frontend\networthy-ui\package.json"
$pkgJson = Get-Content $pkgJsonPath -Raw
$updatedPkg = $pkgJson -replace '("@cortex/ui":\s*")[^"]+(")', "`${1}file:../../.packages/$tgz`${2}"
if ($updatedPkg -ne $pkgJson) {
    Set-Content $pkgJsonPath $updatedPkg -NoNewline
    Write-Host "  ~ networthy-ui package.json -> $tgz"
}

# Bump the pinned versions in the project files.
$csprojs = Get-ChildItem (Join-Path $repoRoot "src") -Recurse -Filter *.csproj
foreach ($proj in $csprojs) {
    $text = Get-Content $proj.FullName -Raw
    $updated = $text -replace '(<PackageReference Include="Cortex\.[^"]+" Version=")[^"]+(")', "`${1}$Version`${2}"
    if ($updated -ne $text) {
        Set-Content $proj.FullName $updated -NoNewline
        Write-Host "  ~ $($proj.Name)"
    }
}

if ($WithUi) {
    # ADR-0008: the APP bundle is Networthy's own build (frontend/networthy-ui, embedded by
    # scripts/build-ui.ps1) — the prebuilt release app shell would drop the Overview dashboard.
    # Only the admin console still vendors prebuilt from the release.
    Write-Host "Fetching the prebuilt admin bundle (the app bundle builds via build-ui.ps1, ADR-0008)..." -ForegroundColor Cyan
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "cortex-ui-$Version"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    foreach ($pair in @(
        @{ Zip = "cortex-admin-ui.zip"; Target = Join-Path $repoRoot "src\Networthy.Host\wwwroot\admin" }
    )) {
        $zipPath = Join-Path $tmp $pair.Zip
        Invoke-WebRequest "$base/$($pair.Zip)" -OutFile $zipPath -UseBasicParsing
        if (Test-Path $pair.Target) { Remove-Item -Recurse -Force $pair.Target -Confirm:$false }
        Expand-Archive $zipPath -DestinationPath $pair.Target
        Write-Host "  + $($pair.Zip) -> $($pair.Target)"
    }
    Remove-Item -Recurse -Force $tmp -Confirm:$false
}

Write-Host "`nNext:" -ForegroundColor Green
Write-Host "  1. Update the version pin in .gitignore's '!.packages/*$Version*' negation line."
Write-Host "  2. ./scripts/build-ui.ps1   (rebuilds the embedded app from the new @cortex/ui tarball)"
Write-Host "  3. dotnet restore Networthy.slnx && dotnet test Networthy.slnx"
Write-Host "  4. Commit .packages/, the csproj bumps, frontend/networthy-ui$(if ($WithUi) { ', and wwwroot/' } else { ', and wwwroot/app' })."
