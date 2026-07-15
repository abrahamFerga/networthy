# Updates the vendored Plenipo platform from a GitHub Release — no Plenipo checkout, no
# registry auth (release assets download anonymously).
#
#   ./scripts/update-platform.ps1 -Version 0.1.0-alpha.22          # nupkgs into .packages/
#   ./scripts/update-platform.ps1 -Version 0.1.0-alpha.22 -WithUi  # + brand-agnostic admin bundle
#
# What it does:
#  1. Downloads every Plenipo.*.nupkg from the release into .packages/ (removing the old version)
#     and bumps the Version="…" references in src/**.csproj.
#  2. Points frontend/networthy-ui's @plenipo/ui dependency at the matching npm version. The UI
#     library ships to the public npm registry (not as a release asset), so the app bundle
#     rebuilds from the installed package via scripts/build-ui.ps1 (ADR-0008, amended).
#  3. With -WithUi: downloads plenipo-admin-ui.zip into src/Networthy.Host/wwwroot/admin (the
#     admin console stays prebuilt; the APP bundle is always Networthy's own build).
#  4. Prints the follow-ups: rebuild UI, restore, test, update .gitignore's pin, commit.
#
# .packages/ and wwwroot/ stay COMMITTED. The nupkgs are only published to GitHub Packages (which
# needs a PAT), so vendoring them keeps `dotnet restore` working on a bare clone. @plenipo/ui is a
# normal public npm dependency, so the frontend restores from the registry like any other package.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$WithUi,
    [string]$Repo = "Plenipo/Plenipo"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$tag = "v$Version"
$base = "https://github.com/$Repo/releases/download/$tag"

$packages = @(
    "Plenipo.Core", "Plenipo.Application", "Plenipo.Infrastructure", "Plenipo.AspNetCore",
    "Plenipo.ServiceDefaults", "Plenipo.Modules.Sdk", "Plenipo.Connectors.Sdk",
    "Plenipo.Connectors", "Plenipo.Cli"
)

Write-Host "Fetching Plenipo $Version from $Repo release $tag..." -ForegroundColor Cyan
$feed = Join-Path $repoRoot ".packages"
# Remove other versions FIRST. Re-vendoring the SAME version (e.g. switching provenance from a
# local pack to the official release) reuses identical filenames — deleting "old" files after
# the download would delete the downloads themselves.
$old = Get-ChildItem $feed -Filter "Plenipo.*.nupkg" | Where-Object { $_.Name -notlike "*.$Version.nupkg" }
$old | Remove-Item -Force -Confirm:$false
Write-Host "  - removed $($old.Count) other-version nupkg(s)"
foreach ($p in $packages) {
    $file = "$p.$Version.nupkg"
    Invoke-WebRequest "$base/$file" -OutFile (Join-Path $feed $file) -UseBasicParsing
    Write-Host "  + $file"
}

# Sweep out the pre-rename feed and the retired UI tarball, so the vendored feed never serves two
# platform generations at once. These filters name the OLD Cortex-era artifacts literally — they
# are on-disk filenames from before the rename, not references to the current platform.
$stale = @(Get-ChildItem $feed -Filter "Cortex.*.nupkg" -ErrorAction SilentlyContinue) +
         @(Get-ChildItem $feed -Filter "cortex-ui-*.tgz" -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) {
    $stale | Remove-Item -Force -Confirm:$false
    Write-Host "  - removed $($stale.Count) pre-rename Cortex artifact(s)"
}

# Point networthy-ui at the matching @plenipo/ui npm version. The library is published to the
# public registry with provenance (ADR-0008, amended) — it is no longer a vendored tarball.
$pkgJsonPath = Join-Path $repoRoot "frontend\networthy-ui\package.json"
$pkgJson = Get-Content $pkgJsonPath -Raw
$updatedPkg = $pkgJson -replace '("@plenipo/ui":\s*")[^"]+(")', "`${1}$Version`${2}"
if ($updatedPkg -ne $pkgJson) {
    Set-Content $pkgJsonPath $updatedPkg -NoNewline
    Write-Host "  ~ networthy-ui package.json -> @plenipo/ui@$Version"
}

# Bump the pinned versions in the project files.
$csprojs = Get-ChildItem (Join-Path $repoRoot "src") -Recurse -Filter *.csproj
foreach ($proj in $csprojs) {
    $text = Get-Content $proj.FullName -Raw
    $updated = $text -replace '(<PackageReference Include="Plenipo\.[^"]+" Version=")[^"]+(")', "`${1}$Version`${2}"
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
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "plenipo-ui-$Version"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    foreach ($pair in @(
        @{ Zip = "plenipo-admin-ui.zip"; Target = Join-Path $repoRoot "src\Networthy.Host\wwwroot\admin" }
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
Write-Host "  2. pnpm -C frontend/networthy-ui install   (resolves @plenipo/ui@$Version from npm)"
Write-Host "  3. ./scripts/build-ui.ps1   (rebuilds the embedded app from @plenipo/ui)"
Write-Host "  4. dotnet restore Networthy.slnx && dotnet test Networthy.slnx"
Write-Host "  5. Re-pin the CSP script-src hashes in src/Networthy.Host/Program.cs if the"
Write-Host "     regenerated index pages changed their inline theme bootstrap."
Write-Host "  6. Commit .packages/, the csproj bumps, frontend/networthy-ui$(if ($WithUi) { ', and wwwroot/' } else { ', and wwwroot/app' })."
