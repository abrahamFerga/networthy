# Updates the vendored Cortex platform from a GitHub Release — no Cortex checkout, no pnpm,
# no registry auth (release assets download anonymously).
#
#   ./scripts/update-platform.ps1 -Version 0.1.0-alpha.14          # nupkgs into .packages/
#   ./scripts/update-platform.ps1 -Version 0.1.0-alpha.14 -WithUi  # + brand-agnostic UI bundles
#
# What it does:
#  1. Downloads every Cortex.*.nupkg from the release into .packages/ (removing the old version)
#     and bumps the Version="…" references in src/**.csproj.
#  2. With -WithUi: downloads cortex-ui-app.zip / cortex-admin-ui.zip into
#     src/Networthy.Host/wwwroot/{app,admin}. These bundles resolve the product name at RUNTIME
#     (Branding:ProductName — already "Networthy" in appsettings.json). The alternative,
#     scripts/build-ui.ps1, builds from a Cortex checkout and BAKES the brand (including the
#     static <title>); use that when you want no unbranded first paint or you're changing the
#     frontend itself.
#  3. Prints the follow-ups: restore, test, update .gitignore's version pin, commit.
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
    Write-Host "Fetching prebuilt UI bundles (runtime-branded via Branding:ProductName)..." -ForegroundColor Cyan
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "cortex-ui-$Version"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    foreach ($pair in @(
        @{ Zip = "cortex-ui-app.zip";   Target = Join-Path $repoRoot "src\Networthy.Host\wwwroot\app" },
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
Write-Host "  2. dotnet restore Networthy.slnx && dotnet test Networthy.slnx"
Write-Host "  3. Commit .packages/, the csproj bumps$(if ($WithUi) { ', and wwwroot/' } else { '' })."
