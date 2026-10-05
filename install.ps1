<#
.SYNOPSIS
  Installs (or removes) the AGS MCP plugins in an Adventure Game Studio 3.6 editor folder.

.DESCRIPTION
  Copies AGS.Plugin.Mcp.dll (the editor plugin that hosts the MCP server) and agsmcp.dll (the
  engine plugin behind the game_* tools) into the folder that contains AGSEditor.exe.

  The plugin files come from the first of:
    1. -Source: a release zip, or a folder holding an unpacked release.
    2. The folder this script is in, when it is run from an unpacked release.
    3. The GitHub release named by -Version (default: the latest).

  Without -AgsDir the script looks for AGS 3.6 in a running editor, the installer's registry
  entries and Program Files. A portable (zip) AGS cannot be found that way: pass -AgsDir.

  Close the AGS editor first: it locks the plugin DLL while it runs.

.EXAMPLE
  irm https://raw.githubusercontent.com/pravusjif/ags-mcp-api/main/install.ps1 | iex

.EXAMPLE
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/pravusjif/ags-mcp-api/main/install.ps1))) -AgsDir C:\AGS-3.6.2

.EXAMPLE
  .\install.ps1 -AgsDir C:\AGS-3.6.2 -InstallSkill    # from an unpacked release
  .\install.ps1 -AgsDir C:\AGS-3.6.2 -InstallSkill -SkillDir <your agent's skills folder>
  .\install.ps1 -AgsDir C:\AGS-3.6.2 -Uninstall
#>
[CmdletBinding()]
param(
    # AGS 3.6.x editor folder (the one with AGSEditor.exe).
    [string]$AgsDir,
    # Release to download, e.g. 0.1.0. Ignored when -Source is given or the script runs from a release.
    [string]$Version = "latest",
    # A release zip or an unpacked release folder to install from instead of downloading.
    [string]$Source,
    # Also install the ags-mcp agent skill (a SKILL.md folder) into -SkillDir.
    [switch]$InstallSkill,
    # Skills folder of your agent. Default: Claude Code's user skills folder (~\.claude\skills).
    [string]$SkillDir = (Join-Path $HOME ".claude\skills"),
    # Remove the plugin DLLs from the AGS folder instead of installing them.
    [switch]$Uninstall,
    # GitHub repository that publishes the releases.
    [string]$Repo = "pravusjif/ags-mcp-api"
)

# Everything runs inside functions so that "irm | iex" leaves the caller's session settings alone.
# Errors are thrown, never "exit", which would close the caller's window under iex.

$script:AgsMcpFiles = @("AGS.Plugin.Mcp.dll", "agsmcp.dll")
$script:AgsMcpEndpoint = "http://127.0.0.1:7471/mcp"

function Get-AgsVersion([string]$Dir) {
    try { return (Get-Item (Join-Path $Dir "AGSEditor.exe")).VersionInfo.FileVersion } catch { return "" }
}

function Find-AgsDir {
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($p in Get-Process AGSEditor -ErrorAction SilentlyContinue) {
        if ($p.Path) { $candidates.Add((Split-Path -Parent $p.Path)) }
    }
    $uninstallKeys = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*")
    Get-ItemProperty $uninstallKeys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "Adventure Game Studio*" -and $_.InstallLocation } |
        ForEach-Object { $candidates.Add($_.InstallLocation.TrimEnd("\")) }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($base -and (Test-Path $base)) {
            Get-ChildItem $base -Directory -Filter "Adventure Game Studio*" -ErrorAction SilentlyContinue |
                ForEach-Object { $candidates.Add($_.FullName) }
        }
    }
    $found = @($candidates | Where-Object { Test-Path (Join-Path $_ "AGSEditor.exe") } |
        Where-Object { (Get-AgsVersion $_) -like "3.6.*" } | Sort-Object -Unique)
    if ($found.Count -eq 1) { return $found[0] }
    if ($found.Count -eq 0) {
        throw "Could not find an AGS 3.6 editor. Pass -AgsDir <folder that contains AGSEditor.exe>."
    }
    throw ("Found several AGS 3.6 editors. Pick one with -AgsDir:`n  " + ($found -join "`n  "))
}

function Resolve-AgsDir([string]$Dir) {
    if (-not $Dir) { $Dir = Find-AgsDir }
    $Dir = (Resolve-Path $Dir -ErrorAction Stop).Path.TrimEnd("\")
    if (-not (Test-Path (Join-Path $Dir "AGSEditor.exe"))) { throw "AGSEditor.exe not found in '$Dir'." }
    $ver = Get-AgsVersion $Dir
    if ($ver -notlike "3.6.*") { Write-Warning "AGSEditor.exe in '$Dir' reports version '$ver'. The plugin targets AGS 3.6.x." }
    $running = Get-Process AGSEditor -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ((Split-Path -Parent $_.Path).TrimEnd("\") -eq $Dir) }
    if ($running) { throw "The AGS editor in '$Dir' is running and locks the plugin. Close it, then run this again." }
    return $Dir
}

function Get-ReleaseFolder([string]$Src, [string]$Ver, [string]$RepoName, [string]$Temp) {
    if ($Src) {
        $Src = (Resolve-Path $Src -ErrorAction Stop).Path
        if (Test-Path $Src -PathType Container) { return $Src }
        Expand-Archive $Src (Join-Path $Temp "release") -Force
        return (Join-Path $Temp "release")
    }
    if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot $script:AgsMcpFiles[0]))) { return $PSScriptRoot }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $api = if ($Ver -eq "latest") { "https://api.github.com/repos/$RepoName/releases/latest" }
           else { "https://api.github.com/repos/$RepoName/releases/tags/v$($Ver.TrimStart('v'))" }
    Write-Host "Looking up release '$Ver' of $RepoName"
    try { $release = Invoke-RestMethod $api -UseBasicParsing }
    catch { throw "Could not read $api ($($_.Exception.Message)). Download the release zip by hand and pass -Source <zip>." }
    $asset = $release.assets | Where-Object { $_.name -like "ags-mcp-*.zip" } | Select-Object -First 1
    if (-not $asset) { throw "Release $($release.tag_name) has no ags-mcp-*.zip asset." }
    $zip = Join-Path $Temp $asset.name
    Write-Host "Downloading $($asset.name)"
    $oldProgress = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"   # Windows PowerShell's progress bar slows downloads badly
    try { Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing }
    finally { $ProgressPreference = $oldProgress }
    Expand-Archive $zip (Join-Path $Temp "release") -Force
    return (Join-Path $Temp "release")
}

function Write-NextSteps {
    $e = $script:AgsMcpEndpoint
    Write-Host ""
    Write-Host "Next steps" -ForegroundColor Cyan
    Write-Host "  1. Start the AGS editor and open your game. An MCP menu appears and the server listens on $e."
    Write-Host "  2. Connect your MCP client (MCP > Client setup in the editor shows the same):"
    Write-Host "       Any Streamable HTTP client:  $e"
    Write-Host "       Claude Code:                 claude mcp add --transport http ags $e"
    Write-Host "       Codex CLI (config.toml):     [mcp_servers.ags]  url = `"$e`""
    Write-Host "       Gemini CLI (settings.json):  { `"mcpServers`": { `"ags`": { `"httpUrl`": `"$e`" } } }"
    Write-Host "       stdio-only clients:          command 'npx', args '-y mcp-remote $e'"
    Write-Host "  3. For the game_* tools, call runtime_enable_plugin and then save_project once per game."
}

function Invoke-AgsMcpInstall {
    $ErrorActionPreference = "Stop"
    $dir = Resolve-AgsDir $AgsDir

    if ($Uninstall) {
        foreach ($f in $script:AgsMcpFiles + @("AGS.Plugin.Mcp.pdb")) {
            $p = Join-Path $dir $f
            if (Test-Path $p) { Remove-Item $p -Force; Write-Host "Removed $p" }
        }
        $skill = Join-Path $SkillDir "ags-mcp"
        if (Test-Path $skill) { Write-Host "The skill at $skill was left in place; delete it by hand if you no longer want it." }
        Write-Host "AGS MCP uninstalled." -ForegroundColor Green
        return
    }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ("ags-mcp-install-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $release = Get-ReleaseFolder $Source $Version $Repo $temp
        foreach ($f in $script:AgsMcpFiles) {
            if (-not (Test-Path (Join-Path $release $f))) { throw "$f is missing from '$release'." }
        }

        try {
            $probe = Join-Path $dir (".ags-mcp-write-test-" + [Guid]::NewGuid().ToString("N"))
            [IO.File]::WriteAllText($probe, "")
            Remove-Item $probe -Force
        } catch {
            throw "Cannot write to '$dir'. Run this from an elevated (Administrator) PowerShell, or install AGS to a folder you own."
        }

        foreach ($f in $script:AgsMcpFiles) {
            $target = Join-Path $dir $f
            Copy-Item (Join-Path $release $f) $target -Force
            Unblock-File $target   # a downloaded DLL keeps the internet-zone mark, and .NET may refuse to load it
            Write-Host "Installed $target"
        }
        $ver = (Get-Item (Join-Path $dir $script:AgsMcpFiles[0])).VersionInfo.FileVersion
        Write-Host "AGS MCP $ver installed into $dir" -ForegroundColor Green

        if ($InstallSkill) {
            $skillSrc = Join-Path $release "skills\ags-mcp"
            if (Test-Path $skillSrc) {
                $skillDst = Join-Path $SkillDir "ags-mcp"
                if (Test-Path $skillDst) { Remove-Item $skillDst -Recurse -Force }
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $skillDst) | Out-Null
                Copy-Item $skillSrc $skillDst -Recurse
                Write-Host "Installed the ags-mcp skill into $skillDst"
            } else {
                Write-Warning "This release has no skills\ags-mcp folder; skipped the skill."
            }
        }
        Write-NextSteps
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-AgsMcpInstall
