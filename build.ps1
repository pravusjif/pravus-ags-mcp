<#
.SYNOPSIS
  Builds the AGS MCP plugins and optionally deploys them into an AGS editor folder.

.EXAMPLE
  .\build.ps1                         # build (Release)
  .\build.ps1 -Test                   # build + unit tests
  .\build.ps1 -Deploy                 # build + copy DLLs into $AgsDir (editor must be closed)
  .\build.ps1 -Deploy -Run            # ...then start the editor (-Game <Game.agf> opens that game)
  .\build.ps1 -Deploy -StopEditor     # close a running editor first (unsaved work is lost!)
  .\build.ps1 -Engine                 # build the native engine plugin (agsmcp.dll) with CMake
  .\build.ps1 -Engine -Deploy         # ...and copy agsmcp.dll into $AgsDir
  .\build.ps1 -Engine -Package        # build both and zip a release into dist\

  -AgsDir defaults to AgsDir in Local.props (see Local.props.example).
#>
param(
    [string]$AgsDir,
    [string]$Game,
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [switch]$Test,
    [switch]$Deploy,
    [switch]$StopEditor,
    [switch]$Run,
    [switch]$Engine,
    [switch]$Package
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
. "$root\tools\LocalProps.ps1"
if (-not $AgsDir) { $AgsDir = Get-LocalProp AgsDir }

if (-not $AgsDir -or -not (Test-Path (Join-Path $AgsDir "AGSEditor.exe"))) {
    throw "AGSEditor.exe not found in '$AgsDir'. Set AgsDir in Local.props (copy Local.props.example) or pass -AgsDir <path to your AGS 3.6 folder>."
}

Write-Host "Building editor plugin ($Configuration) against $AgsDir" -ForegroundColor Cyan
dotnet build "$root\src\AgsMcp.Editor\AgsMcp.Editor.csproj" -c $Configuration "-p:AgsDir=$AgsDir" -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Editor plugin build failed" }
$pluginDll = "$root\src\AgsMcp.Editor\bin\$Configuration\AGS.Plugin.Mcp.dll"

$engineDll = "$root\src\AgsMcp.Engine\build\$Configuration\agsmcp.dll"
if ($Engine) {
    Write-Host "Building native engine plugin (agsmcp.dll, x86)" -ForegroundColor Cyan
    $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
    if (-not $cmake) {
        $cmake = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    }
    if (-not (Test-Path $cmake)) { throw "cmake not found. Install it or VS 2022 with C++ CMake tools." }
    $engineSrc = "$root\src\AgsMcp.Engine"
    $engineBuild = "$engineSrc\build"
    & $cmake -S $engineSrc -B $engineBuild -A Win32 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Engine CMake configure failed" }
    & $cmake --build $engineBuild --config $Configuration | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Engine build failed" }
    if (-not (Test-Path $engineDll)) { throw "Engine build produced no agsmcp.dll at $engineDll" }
    Write-Host "Built $engineDll" -ForegroundColor Green
}

if ($Test) {
    Write-Host "Running unit tests" -ForegroundColor Cyan
    dotnet test "$root\tests\AgsMcp.Editor.Tests" "-p:AgsDir=$AgsDir" -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
}

if ($Package) {
    if (-not (Test-Path $engineDll)) { throw "Packaging needs agsmcp.dll. Add -Engine." }
    $version = ([xml](Get-Content "$root\Directory.Build.props" -Raw)).SelectSingleNode("//Version").InnerText
    $name = "ags-mcp-$version"
    $dist = "$root\dist"
    $stage = "$dist\$name"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path "$stage\skills" | Out-Null
    Copy-Item $pluginDll, $engineDll, "$root\install.ps1", "$root\README.md", "$root\LICENSE" $stage
    Copy-Item "$root\skills\ags-mcp" "$stage\skills\ags-mcp" -Recurse
    Remove-Item "$stage\skills\ags-mcp\evals" -Recurse -Force -ErrorAction SilentlyContinue
    $zip = "$dist\$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Entries are added one by one because Windows PowerShell's ZipFile.CreateFromDirectory writes "\" separators.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $entry = $file.FullName.Substring($stage.Length + 1).Replace("\", "/")
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
        }
    } finally { $archive.Dispose() }
    Remove-Item $stage -Recurse -Force
    Write-Host "Packaged $zip" -ForegroundColor Green
}

if ($Deploy) {
    $editor = Get-Process AGSEditor -ErrorAction SilentlyContinue
    if ($editor) {
        if (-not $StopEditor) {
            throw "The AGS editor is running and locks the plugin DLL. Close it (or pass -StopEditor) and retry."
        }
        Write-Host "Stopping the AGS editor" -ForegroundColor Yellow
        $editor | Stop-Process -Force
        $editor | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }
    # A just-stopped editor can hold the DLL for a moment after its process exits, so retry briefly.
    for ($attempt = 1; ; $attempt++) {
        try { Copy-Item $pluginDll $AgsDir -Force -ErrorAction Stop; break }
        catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 500 }
    }
    Copy-Item ([IO.Path]::ChangeExtension($pluginDll, ".pdb")) $AgsDir -Force
    Write-Host "Deployed AGS.Plugin.Mcp.dll to $AgsDir" -ForegroundColor Green
    if (Test-Path $engineDll) {
        Copy-Item $engineDll $AgsDir -Force
        Write-Host "Deployed agsmcp.dll to $AgsDir" -ForegroundColor Green
    }
}

if ($Run) {
    $editor = Join-Path $AgsDir "AGSEditor.exe"
    # Start-Process rejects an empty -ArgumentList, so pass it only when there is a game to open.
    if ($Game -and (Test-Path $Game)) { Start-Process $editor -ArgumentList "`"$Game`"" -WorkingDirectory $AgsDir }
    else { Start-Process $editor -WorkingDirectory $AgsDir }
    Write-Host "Started the AGS editor. MCP endpoint: http://127.0.0.1:7471/mcp" -ForegroundColor Green
}
