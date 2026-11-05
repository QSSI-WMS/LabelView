<#
.SYNOPSIS
    One-time setup of the public-read S3 prefix that hosts LabelView
    auto-update assets.

.DESCRIPTION
    Creates the bucket (if missing), allows public bucket policies, and grants
    anonymous s3:GetObject on the LabelView release prefix ONLY.

    The bucket is shared with other tools (e.g. MultiFind2), so this script
    MERGES its statement into the existing bucket policy instead of replacing
    it. Statements belonging to other tools are left untouched.

    Run once with AWS credentials configured (aws configure). Safe to re-run.

.NOTES
    Requires: aws CLI, configured with a profile that can create/manage the bucket.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Keep in sync with AutoUpdater.BaseUrl and publish-release.ps1.
$Bucket = 'qssi-tools-autoupdate'
$Region = 'us-east-2'
$Prefix = 'labelview/8a87673bba6101b9009c654b'
$Sid    = 'PublicReadLabelViewUpdates'

# --- Create the bucket if it does not exist ----------------------------------
$exists = $true
try { aws s3api head-bucket --bucket $Bucket --region $Region 2>$null; if ($LASTEXITCODE -ne 0) { $exists = $false } }
catch { $exists = $false }

if (-not $exists) {
    Write-Host "Creating bucket $Bucket in $Region ..." -ForegroundColor Yellow
    if ($Region -eq 'us-east-1') {
        aws s3api create-bucket --bucket $Bucket --region $Region
    } else {
        aws s3api create-bucket --bucket $Bucket --region $Region `
            --create-bucket-configuration "LocationConstraint=$Region"
    }
    if ($LASTEXITCODE -ne 0) { throw "create-bucket failed (name may be taken globally - pick another)" }
} else {
    Write-Host "Bucket $Bucket already exists." -ForegroundColor Green
}

# --- Allow public bucket policies (leave ACLs blocked) -----------------------
Write-Host "Allowing public bucket policy ..." -ForegroundColor Yellow
aws s3api put-public-access-block --bucket $Bucket --region $Region `
    --public-access-block-configuration "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=false,RestrictPublicBuckets=false"
if ($LASTEXITCODE -ne 0) { throw "put-public-access-block failed" }

# --- Merge public read on this prefix into the existing policy ---------------
$statements = @()
$existing = aws s3api get-bucket-policy --bucket $Bucket --region $Region --query Policy --output text 2>$null
if ($LASTEXITCODE -eq 0 -and $existing) {
    $statements = @((ConvertFrom-Json $existing).Statement | Where-Object { $_.Sid -ne $Sid })
    Write-Host ("Keeping {0} existing policy statement(s)." -f $statements.Count) -ForegroundColor Green
}
$statements += [ordered]@{
    Sid       = $Sid
    Effect    = 'Allow'
    Principal = '*'
    Action    = 's3:GetObject'
    Resource  = "arn:aws:s3:::$Bucket/$Prefix/*"
}
$policy = [ordered]@{ Version = '2012-10-17'; Statement = $statements } | ConvertTo-Json -Depth 10

$policyFile = Join-Path $env:TEMP 'labelview-bucket-policy.json'
[System.IO.File]::WriteAllText($policyFile, $policy, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Applying bucket policy:" -ForegroundColor Yellow
Write-Host $policy
aws s3api put-bucket-policy --bucket $Bucket --region $Region --policy "file://$policyFile"
if ($LASTEXITCODE -ne 0) { throw "put-bucket-policy failed" }
Remove-Item $policyFile -Force -ErrorAction SilentlyContinue

Write-Host "Bucket ready: https://$Bucket.s3.$Region.amazonaws.com/$Prefix/" -ForegroundColor Cyan
