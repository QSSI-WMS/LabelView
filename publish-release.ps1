<#
.SYNOPSIS
    Build both LabelView executables, publish them to S3 (where the auto-updater
    looks), and cut the matching GitHub release.

.DESCRIPTION
    Produces two single-file exes:
      * LabelView-full.exe  - self-contained, needs no .NET runtime
      * LabelView.exe       - framework-dependent, needs .NET 8 runtime

    Uploads both plus a latest.json manifest to the public-read S3 prefix that
    AutoUpdater.BaseUrl points at, then creates a GitHub release with both exes.

    The S3 settings below MUST match AutoUpdater.BaseUrl in the client, and the
    prefix must already be public (run setup-s3-bucket.ps1 once first).

.NOTES
    Requires: dotnet, aws CLI (configured), gh CLI (authenticated).
#>
[CmdletBinding()]
param(
    # Skip the GitHub release step (S3 upload only).
    [switch]$SkipGitHubRelease,
    # Skip the S3 upload step (GitHub release only).
    [switch]$SkipS3
)

$ErrorActionPreference = 'Stop'

# --- S3 target (keep in sync with AutoUpdater.BaseUrl) -----------------------
$Bucket  = 'qssi-tools-autoupdate'
$Region  = 'us-east-2'
$Prefix  = 'labelview/8a87673bba6101b9009c654b'
$BaseUrl = "https://$Bucket.s3.$Region.amazonaws.com/$Prefix/"

$Root    = $PSScriptRoot
$Csproj  = Join-Path $Root 'LabelView\LabelView.csproj'
$Stage   = Join-Path $Root 'release-staging'

# --- Version from the csproj -------------------------------------------------
[xml]$proj = Get-Content $Csproj
$Version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $Version) { throw "Could not read <Version> from $Csproj" }
Write-Host "Releasing LabelView v$Version" -ForegroundColor Cyan

# --- Clean staging -----------------------------------------------------------
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Path $Stage | Out-Null

# --- Build: full (self-contained) -------------------------------------------
Write-Host "Building full (self-contained) exe..." -ForegroundColor Yellow
dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $Root 'publish-full')
if ($LASTEXITCODE -ne 0) { throw "full build failed" }
Copy-Item (Join-Path $Root 'publish-full\LabelView.exe') (Join-Path $Stage 'LabelView-full.exe') -Force

# --- Build: small (framework-dependent) -------------------------------------
Write-Host "Building small (framework-dependent) exe..." -ForegroundColor Yellow
dotnet publish $Csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -o (Join-Path $Root 'publish-small')
if ($LASTEXITCODE -ne 0) { throw "small build failed" }
Copy-Item (Join-Path $Root 'publish-small\LabelView.exe') (Join-Path $Stage 'LabelView.exe') -Force

$fullExe  = Join-Path $Stage 'LabelView-full.exe'
$smallExe = Join-Path $Stage 'LabelView.exe'
$fullSize  = (Get-Item $fullExe).Length
$smallSize = (Get-Item $smallExe).Length
Write-Host ("  full : {0:N0} bytes" -f $fullSize)
Write-Host ("  small: {0:N0} bytes" -f $smallSize)

# --- Manifest (latest.json) --------------------------------------------------
$manifest = [ordered]@{
    version = $Version
    assets  = @(
        [ordered]@{ name = 'LabelView.exe';      url = "$BaseUrl" + 'LabelView.exe';      size = $smallSize }
        [ordered]@{ name = 'LabelView-full.exe'; url = "$BaseUrl" + 'LabelView-full.exe'; size = $fullSize  }
    )
}
$manifestPath = Join-Path $Stage 'latest.json'
# Write UTF-8 WITHOUT a BOM; a leading BOM trips strict JSON parsers.
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
Write-Host "Wrote manifest:" -ForegroundColor Yellow
Get-Content $manifestPath | Write-Host

# --- Upload to S3 ------------------------------------------------------------
if (-not $SkipS3) {
    Write-Host "Uploading to s3://$Bucket/$Prefix/ ..." -ForegroundColor Yellow
    # Bucket grants public read via policy; no per-object ACL needed.
    aws s3 cp $smallExe     "s3://$Bucket/$Prefix/LabelView.exe"      --region $Region --content-type 'application/octet-stream'
    if ($LASTEXITCODE -ne 0) { throw "upload of small exe failed" }
    aws s3 cp $fullExe      "s3://$Bucket/$Prefix/LabelView-full.exe" --region $Region --content-type 'application/octet-stream'
    if ($LASTEXITCODE -ne 0) { throw "upload of full exe failed" }
    # Upload the manifest LAST and with no-cache so clients always see a fresh version.
    aws s3 cp $manifestPath "s3://$Bucket/$Prefix/latest.json" --region $Region `
        --content-type 'application/json' --cache-control 'no-cache, max-age=0'
    if ($LASTEXITCODE -ne 0) { throw "upload of manifest failed" }
    Write-Host "S3 upload complete." -ForegroundColor Green
}

# --- GitHub release ----------------------------------------------------------
if (-not $SkipGitHubRelease) {
    $tag = "v$Version"
    Write-Host "Creating GitHub release $tag ..." -ForegroundColor Yellow
    $notes = "LabelView $tag`n`n" +
             "- LabelView.exe - framework-dependent (needs .NET 8 runtime)`n" +
             "- LabelView-full.exe - self-contained (no runtime needed)`n`n" +
             "Auto-update pulls from S3; this release mirrors the same binaries."
    gh release create $tag $smallExe $fullExe --title "LabelView $tag" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
    Write-Host "GitHub release $tag created." -ForegroundColor Green
}

Write-Host "Done." -ForegroundColor Cyan
