# binary-common.ps1 - shared binary resolution logic (Windows).
#
# Dot-sourced by:
#   scripts/ensure-binary.ps1  (SessionStart hook)
#   scripts/run-server.ps1     (MCP launcher - runs on every server start/reconnect)
#
# Layout on disk (versioned, see docs/GH-RELEASE-SETUP.md):
#   <plugin-data>/bin/<version>/DataverseMcp.exe   <- what actually gets executed
#   <plugin-data>/bin/DataverseMcp.exe             <- legacy path, best-effort copy
#   <plugin-data>/bin/.version                     <- legacy marker
#
# Versioned directories exist because Windows cannot overwrite a running .exe:
# a second Claude session (or a reconnect while the old process is shutting down)
# would otherwise fail to install a new version. A new version lands in a new
# directory, so nothing is ever overwritten in place.
#
# IMPORTANT: every diagnostic goes to stderr. The launcher shares stdout with the
# MCP stdio stream - a single stray line on stdout breaks the protocol.

$ErrorActionPreference = 'Stop'

$script:AssetName   = 'DataverseMcp-win-x64.exe'  # release asset (RID-suffixed)
$script:RuntimeName = 'DataverseMcp.exe'          # on-disk name

function Write-Note($msg)
{
    [Console]::Error.WriteLine("[dataverse-modelling-mcp] $msg")
}

function Get-ExpectedVersion($pluginRoot)
{
    $versionFile = Join-Path $pluginRoot 'scripts\BINARY_VERSION'
    if (-not (Test-Path $versionFile))
    {
        throw "BINARY_VERSION not found at $versionFile."
    }

    $version = (Get-Content $versionFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($version) -or $version -eq '0.0.0')
    {
        throw "BINARY_VERSION is '$version' - no published binary to download. Publish a release first."
    }

    return $version
}

function Get-InstalledBinaryFallback($binDir)
{
    # Newest usable binary already on disk, used when the download is impossible
    # (offline / no GitHub auth). Prefers the highest version directory, then the
    # legacy flat path.
    if (-not (Test-Path $binDir))
    {
        return $null
    }

    $candidates = Get-ChildItem -Path $binDir -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $exe = Join-Path $_.FullName $script:RuntimeName
            if ((Test-Path $exe) -and (Get-Item $exe).Length -gt 0)
            {
                $parsed = $null
                [void][Version]::TryParse($_.Name, [ref]$parsed)
                [pscustomobject]@{ Path = $exe; Version = $parsed }
            }
        } | Sort-Object -Property Version -Descending

    if ($candidates)
    {
        return $candidates[0].Path
    }

    $legacy = Join-Path $binDir $script:RuntimeName
    if ((Test-Path $legacy) -and (Get-Item $legacy).Length -gt 0)
    {
        return $legacy
    }

    return $null
}

function Get-BinaryFromGitHub($repo, $tag, $destination)
{
    $tmp = "$destination.download"
    if (Test-Path $tmp) { Remove-Item $tmp -Force }

    Write-Note "fetching $script:AssetName ($tag) from $repo ..."
    $downloaded = $false

    # Preferred: gh CLI (reuses the user's existing GitHub auth - the same auth that
    # powers 'claude plugin marketplace add' for private repos).
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh)
    {
        try
        {
            & $gh.Source release download $tag --repo $repo --pattern $script:AssetName --output $tmp --clobber 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0 -and (Test-Path $tmp)) { $downloaded = $true }
        }
        catch { }
    }

    # Fallback: REST API with a token. Resolve the asset id, then download by id with
    # Accept: application/octet-stream (works for private repos).
    if (-not $downloaded)
    {
        $token = $env:GITHUB_TOKEN
        if ([string]::IsNullOrWhiteSpace($token)) { $token = $env:GH_TOKEN }
        if ([string]::IsNullOrWhiteSpace($token))
        {
            throw "gh CLI unavailable/unauthenticated and neither GITHUB_TOKEN nor GH_TOKEN is set. Run 'gh auth login' or set a token."
        }

        $headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'dataverse-modelling-mcp' }
        try
        {
            $rel = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$repo/releases/tags/$tag"
        }
        catch
        {
            throw "could not resolve release $tag in $repo. $($_.Exception.Message)"
        }

        $assetObj = $rel.assets | Where-Object { $_.name -eq $script:AssetName } | Select-Object -First 1
        if (-not $assetObj)
        {
            throw "release $tag has no asset named '$script:AssetName'."
        }

        $dlHeaders = @{ Authorization = "Bearer $token"; 'User-Agent' = 'dataverse-modelling-mcp'; Accept = 'application/octet-stream' }
        try
        {
            Invoke-WebRequest -Headers $dlHeaders -Uri $assetObj.url -OutFile $tmp
        }
        catch
        {
            throw "download failed while fetching asset: $($_.Exception.Message)"
        }
    }

    if (-not (Test-Path $tmp) -or (Get-Item $tmp).Length -eq 0)
    {
        throw "download produced an empty file."
    }

    Move-Item -Force $tmp $destination
}

function Remove-StaleVersionDirectories($binDir, $keepVersion)
{
    # Best-effort cleanup. Directories whose binary is still executed by another
    # session stay locked - skipping them silently is correct, the next run retries.
    Get-ChildItem -Path $binDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne $keepVersion } |
        ForEach-Object {
            try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop }
            catch { }
        }
}

<#
.SYNOPSIS
Ensure the MCP server binary for the expected version is on disk and return its path.

.DESCRIPTION
Fast path: when <plugin-data>/bin/<version>/DataverseMcp.exe already exists, the
function returns immediately without any network call - so running it from both
the SessionStart hook and the launcher costs nothing the second time.

.PARAMETER AllowOffline
When the download fails, fall back to whatever binary is already installed and
warn on stderr instead of throwing. Used by the launcher so offline sessions keep
working; the hook stays strict and fails loudly.
#>
function Resolve-DataverseBinary
{
    param(
        [Parameter(Mandatory = $true)] [string] $PluginRoot,
        [Parameter(Mandatory = $true)] [string] $PluginData,
        [switch] $AllowOffline
    )

    $version = Get-ExpectedVersion $PluginRoot
    $binDir     = Join-Path $PluginData 'bin'
    $versionDir = Join-Path $binDir $version
    $target     = Join-Path $versionDir $script:RuntimeName

    if ((Test-Path $target) -and (Get-Item $target).Length -gt 0)
    {
        return $target
    }

    $repo = $env:CLAUDE_PLUGIN_OPTION_RELEASE_REPO
    if ([string]::IsNullOrWhiteSpace($repo)) { $repo = 'hydr/dataverse-modelling-mcp' }

    New-Item -ItemType Directory -Force -Path $versionDir | Out-Null

    try
    {
        Get-BinaryFromGitHub $repo "v$version" $target
    }
    catch
    {
        # Do not leave an empty version directory behind - it would look like an install.
        if ((Test-Path $versionDir) -and -not (Get-ChildItem -Path $versionDir -Force))
        {
            Remove-Item $versionDir -Force -ErrorAction SilentlyContinue
        }

        $fallback = if ($AllowOffline) { Get-InstalledBinaryFallback $binDir } else { $null }
        if ($fallback)
        {
            Write-Note "could not install v$version ($($_.Exception.Message)); starting the already installed binary instead."
            return $fallback
        }
        throw "download failed: $($_.Exception.Message)"
    }

    # Legacy flat path kept in sync for installs whose .mcp.json still points at
    # <plugin-data>/bin/DataverseMcp.exe. Fails while that file is being executed
    # by another session - harmless, the versioned copy is what we run.
    try
    {
        Copy-Item -Force $target (Join-Path $binDir $script:RuntimeName)
        Set-Content -Path (Join-Path $binDir '.version') -Value $version -NoNewline
    }
    catch
    {
        Write-Note "note: legacy bin\$script:RuntimeName not refreshed (in use by another session)."
    }

    Remove-StaleVersionDirectories $binDir $version

    Write-Note "installed $script:RuntimeName v$version."
    return $target
}
