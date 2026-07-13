# ensure-binary.ps1 - SessionStart hook (Windows).
#
# Downloads the self-contained MCP server binary from a GitHub Release and places
# it where .mcp.json expects it (${CLAUDE_PLUGIN_DATA}\bin\DataverseMcp.exe).
# Idempotent: skips the download when the correct version is already present.
#
# See docs/GH-RELEASE-SETUP.md for the publish flow.

$ErrorActionPreference = 'Stop'

function Fail($msg) {
    Write-Error "[dataverse-modelling-mcp] $msg"
    exit 1
}

$pluginRoot = $env:CLAUDE_PLUGIN_ROOT
$pluginData = $env:CLAUDE_PLUGIN_DATA
if ([string]::IsNullOrWhiteSpace($pluginRoot)) { Fail 'CLAUDE_PLUGIN_ROOT is not set.' }
if ([string]::IsNullOrWhiteSpace($pluginData)) { Fail 'CLAUDE_PLUGIN_DATA is not set.' }

$versionFile = Join-Path $pluginRoot 'scripts\BINARY_VERSION'
if (-not (Test-Path $versionFile)) { Fail "BINARY_VERSION not found at $versionFile." }
$version = (Get-Content $versionFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version) -or $version -eq '0.0.0') {
    Fail "BINARY_VERSION is '$version' - no published binary to download. Publish a release first."
}

$repo = $env:CLAUDE_PLUGIN_OPTION_RELEASE_REPO
if ([string]::IsNullOrWhiteSpace($repo)) { $repo = 'hydr/dataverse-modelling-mcp' }

$asset      = 'DataverseMcp-win-x64.exe'   # release asset name (RID-suffixed)
$runtimeName = 'DataverseMcp.exe'          # on-disk name expected by .mcp.json
$binDir      = Join-Path $pluginData 'bin'
$target      = Join-Path $binDir $runtimeName
$marker      = Join-Path $binDir '.version'

# Already up to date?
if ((Test-Path $target) -and (Test-Path $marker)) {
    $have = (Get-Content $marker -Raw).Trim()
    if ($have -eq $version) {
        Write-Host "[dataverse-modelling-mcp] binary v$version already present."
        exit 0
    }
}

New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$tmp = "$target.download"
if (Test-Path $tmp) { Remove-Item $tmp -Force }

$tag = "v$version"
Write-Host "[dataverse-modelling-mcp] fetching $asset ($tag) from $repo ..."

$downloaded = $false

# Preferred: gh CLI (reuses the user's existing GitHub auth - the same auth that
# powers 'claude plugin marketplace add' for private repos).
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh) {
    try {
        & $gh.Source release download $tag --repo $repo --pattern $asset --output $tmp --clobber 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Test-Path $tmp)) { $downloaded = $true }
    } catch { }
}

# Fallback: REST API with a token. Resolve the asset id, then download by id with
# Accept: application/octet-stream (works for private repos).
if (-not $downloaded) {
    $token = $env:GITHUB_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) { $token = $env:GH_TOKEN }
    if ([string]::IsNullOrWhiteSpace($token)) {
        Fail "download failed: gh CLI unavailable/unauthenticated and neither GITHUB_TOKEN nor GH_TOKEN is set. Run 'gh auth login' or set a token."
    }
    $headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'dataverse-modelling-mcp' }
    try {
        $rel = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$repo/releases/tags/$tag"
    } catch {
        Fail "download failed: could not resolve release $tag in $repo. $($_.Exception.Message)"
    }
    $assetObj = $rel.assets | Where-Object { $_.name -eq $asset } | Select-Object -First 1
    if (-not $assetObj) { Fail "download failed: release $tag has no asset named '$asset'." }
    $dlHeaders = @{ Authorization = "Bearer $token"; 'User-Agent' = 'dataverse-modelling-mcp'; Accept = 'application/octet-stream' }
    try {
        Invoke-WebRequest -Headers $dlHeaders -Uri $assetObj.url -OutFile $tmp
        $downloaded = $true
    } catch {
        Fail "download failed while fetching asset: $($_.Exception.Message)"
    }
}

if (-not (Test-Path $tmp) -or (Get-Item $tmp).Length -eq 0) {
    Fail "download produced an empty file - aborting."
}

Move-Item -Force $tmp $target
Set-Content -Path $marker -Value $version -NoNewline
Write-Host "[dataverse-modelling-mcp] installed $runtimeName v$version."
exit 0
