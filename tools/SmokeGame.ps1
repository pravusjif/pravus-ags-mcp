# Provides the throwaway game that build.ps1 -Run opens when no -Game is given, so the MCP tools (which refuse
# every call while no game is open) always have a project. Dot-source it, then call Get-SmokeGame <AgsDir>.
#
# The game is SmokeGame from Local.props when set, else %LOCALAPPDATA%\AGS-MCP\SmokeGame\Game.agf. When the file
# is missing it is created by extracting an editor template (.agt), as File > New Game does before it loads the
# game. The extracted game keeps the template's game ID, which its rooms already carry, so it opens as is.

. (Join-Path $PSScriptRoot "LocalProps.ps1")

$script:DefaultSmokeGame = Join-Path $env:LOCALAPPDATA "AGS-MCP\SmokeGame\Game.agf"

# Extracts an AGS template (an asset library, format version 30 as in AGS 3.6: Common/util/multifilelib.cpp)
# into $Destination, skipping template.dta, as the editor's extract_template_files does (AGS.Native/agsnative.cpp).
function Expand-AgsTemplate([string]$Template, [string]$Destination) {
    $stream = [IO.File]::OpenRead($Template)
    try {
        $r = New-Object IO.BinaryReader($stream)
        $readCStr = {
            $bytes = New-Object Collections.Generic.List[byte]
            while (($b = $r.ReadByte()) -ne 0) { $bytes.Add($b) }
            [Text.Encoding]::UTF8.GetString($bytes.ToArray())
        }
        $sig = [Text.Encoding]::ASCII.GetString($r.ReadBytes(5))
        $version = $r.ReadByte()
        $index = $r.ReadByte()
        if ($sig -ne "CLIB$([char]0x1a)" -or $version -ne 30 -or $index -ne 0) {
            throw "'$Template' is not an AGS 3.6 template (asset library version 30)."
        }
        [void]$r.ReadInt32()  # reserved flags
        $parts = $r.ReadInt32()
        for ($i = 0; $i -lt $parts; $i++) { [void](& $readCStr) }
        $count = $r.ReadInt32()
        $assets = for ($i = 0; $i -lt $count; $i++) {
            $name = & $readCStr
            $lib = $r.ReadByte()
            [pscustomobject]@{ Name = $name; Lib = $lib; Offset = $r.ReadInt64(); Size = $r.ReadInt64() }
        }

        New-Item -ItemType Directory -Force $Destination | Out-Null
        foreach ($a in $assets) {
            if (-not $a.Name -or $a.Name -eq "template.dta") { continue }
            if ($a.Lib -ne 0) { throw "'$Template' spans several files, which templates never do." }
            $out = Join-Path $Destination $a.Name
            New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
            $stream.Position = $a.Offset
            $w = [IO.File]::Create($out)
            try {
                $buf = New-Object byte[] 65536
                $left = $a.Size
                while ($left -gt 0) {
                    $n = $stream.Read($buf, 0, [int][Math]::Min($buf.Length, $left))
                    if ($n -le 0) { throw "'$Template' ends inside '$($a.Name)'." }
                    $w.Write($buf, 0, $n)
                    $left -= $n
                }
            } finally { $w.Dispose() }
        }
    } finally { $stream.Dispose() }
    # The folders Utilities.EnsureStandardSubFoldersExist makes for a new game.
    foreach ($d in "Speech", "AudioCache", "Compiled\Data") { New-Item -ItemType Directory -Force (Join-Path $Destination $d) | Out-Null }
}

# Returns the smoke game's Game.agf, creating it from $Template (a name in <AgsDir>\Templates) when it is missing.
function Get-SmokeGame([string]$AgsDir, [string]$Template = "Sierra-style") {
    $game = Get-LocalProp SmokeGame
    if (-not $game) { $game = $script:DefaultSmokeGame }
    if (Test-Path $game) { return $game }

    $agt = Join-Path $AgsDir "Templates\$Template.agt"
    if (-not (Test-Path $agt)) { throw "Cannot create the smoke game: template '$agt' not found." }
    $folder = Split-Path -Parent $game
    if ((Test-Path $folder) -and (Get-ChildItem $folder -Force | Select-Object -First 1)) {
        throw "Cannot create the smoke game: '$folder' exists, is not empty and has no $(Split-Path -Leaf $game)."
    }
    Expand-AgsTemplate $agt $folder
    $agf = Get-ChildItem $folder -Filter *.agf | Select-Object -First 1
    if (-not $agf) { throw "Template '$agt' has no .agf." }
    if ($agf.FullName -ne $game) { Move-Item $agf.FullName $game }
    Write-Host "Created the smoke game $game from $Template" -ForegroundColor Green
    return $game
}
