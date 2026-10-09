<#
.SYNOPSIS
  End-to-end smoke test: drive a live AGS editor (with the MCP plugin) through every tool group
  over HTTP, on a throwaway game it creates itself.

.DESCRIPTION
  Requires the AGS editor running with the MCP server up (build.ps1 -Engine -Deploy -Run). The engine
  plugin (agsmcp.dll) must be deployed next to AGSEditor.exe so runtime_enable_plugin can find it.

  The test creates a new game from the Sierra-style template in a temp folder (create_project), runs
  every check against it, then reopens the game that was open before and deletes the temp game. No
  existing game is modified, except that the game open at the start is saved first, as File > New Game
  does. Pass -Keep to leave the temp game open for inspection.

.EXAMPLE
  .\tests\smoke\smoke.ps1
  .\tests\smoke\smoke.ps1 -Endpoint http://127.0.0.1:7471/mcp -Keep
#>
param(
    [string]$Endpoint = "http://127.0.0.1:7471/mcp",
    [string]$Folder = (Join-Path ([IO.Path]::GetTempPath()) ("ags-mcp-smoke-" + (Get-Random))),
    [switch]$Keep
)

$ErrorActionPreference = "Stop"
$script:pass = 0
$script:fail = 0
$script:reqId = 0

function Invoke-Mcp {
    param([string]$Name, [hashtable]$Arguments = @{}, [int]$TimeoutSec = 120)
    $script:reqId++
    $payload = @{ jsonrpc = "2.0"; id = $script:reqId; method = "tools/call"; params = @{ name = $Name; arguments = $Arguments } }
    $body = $payload | ConvertTo-Json -Depth 20 -Compress
    $resp = Invoke-WebRequest $Endpoint -Method Post -ContentType application/json -Body $body -TimeoutSec $TimeoutSec -UseBasicParsing
    $parsed = $resp.Content | ConvertFrom-Json
    if ($parsed.error) { throw "JSON-RPC error: $($parsed.error.message)" }
    if ($parsed.result.isError) { throw "$Name failed: $(($parsed.result.content | Select-Object -First 1).text)" }
    return $parsed.result
}

function Get-Text { param($Result) ($Result.content | Where-Object { $_.type -eq "text" } | Select-Object -First 1).text }
function Get-Json { param($Result) Get-Text $Result | ConvertFrom-Json }
function Has-Image { param($Result) [bool]($Result.content | Where-Object { $_.type -eq "image" }) }

# A short fading sine as a 16-bit mono 22050 Hz PCM WAV.
function New-TestWav {
    param([string]$Path, [double]$Hz, [int]$Milliseconds)
    $rate = 22050
    $count = [int]($rate * $Milliseconds / 1000)
    $buf = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($buf)
    $w.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $w.Write([int](36 + $count * 2)); $w.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
    $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes("data")); $w.Write([int]($count * 2))
    for ($i = 0; $i -lt $count; $i++) {
        $w.Write([int16](12000 * (1 - $i / $count) * [Math]::Sin(2 * [Math]::PI * $Hz * $i / $rate)))
    }
    $w.Flush()
    [IO.File]::WriteAllBytes($Path, $buf.ToArray())
    $w.Dispose()
}

function Check {
    param([string]$Label, [scriptblock]$Test)
    try {
        $ok = & $Test
        if ($ok) { Write-Host ("  [PASS] " + $Label) -ForegroundColor Green; $script:pass++ }
        else { Write-Host ("  [FAIL] " + $Label) -ForegroundColor Red; $script:fail++ }
    } catch {
        Write-Host ("  [FAIL] " + $Label + " -- " + $_.Exception.Message) -ForegroundColor Red
        $script:fail++
    }
}

# The game open before the run, reopened afterwards (none if the editor had no game open).
$previousGame = $null
try { $previousGame = (Get-Json (Invoke-Mcp "project_info")).projectFolder } catch { }

function Remove-SmokeGame {
    try { Invoke-Mcp "stop_game" | Out-Null } catch { }
    if (-not $previousGame) {
        Write-Host "No game was open before the run, so the smoke game stays open at $Folder. Delete it after closing it." -ForegroundColor Yellow
        return
    }
    $current = $null
    try { $current = (Get-Json (Invoke-Mcp "project_info")).projectFolder } catch { }
    if ($current -and (Split-Path -Leaf $current) -eq (Split-Path -Leaf $Folder)) {
        Write-Host "Reopening $previousGame and deleting the smoke game..." -ForegroundColor DarkGray
        try { Invoke-Mcp "open_project" @{ path = $previousGame; saveCurrent = $false } 180 | Out-Null }
        catch { Write-Host "Could not reopen ${previousGame}: $($_.Exception.Message)" -ForegroundColor Red; return }
    }
    for ($i = 0; $i -lt 5 -and (Test-Path $Folder); $i++) {
        Remove-Item $Folder -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $Folder) { Start-Sleep -Seconds 2 }
    }
    if (Test-Path $Folder) { Write-Host "Could not delete $Folder (files still in use); delete it by hand." -ForegroundColor Yellow }
}

Write-Host "AGS MCP smoke test against $Endpoint" -ForegroundColor Cyan
Write-Host "Smoke game: $Folder" -ForegroundColor DarkGray

try {
    # ---- Project ----
    Write-Host "Project" -ForegroundColor Cyan
    $created = $false
    Check "create_project makes and opens a new game from the Sierra-style template" {
        $r = Get-Json (Invoke-Mcp "create_project" @{ folder = $Folder; template = "Sierra-style"; gameName = "MCP Smoke" } 300)
        $script:created = $r.created -eq $true
        $script:created -and $r.rooms -ge 1
    }
    if (-not $created) { throw "create_project failed, so there is no game to test against." }
    Check "project_info reports the new game" {
        $info = Get-Json (Invoke-Mcp "project_info")
        $info.gameName -eq "MCP Smoke" -and (Split-Path -Leaf $info.projectFolder) -eq (Split-Path -Leaf $Folder)
    }
    Check "list_entities character lists cEgo" {
        $e = Get-Json (Invoke-Mcp "list_entities" @{ type = "character" })
        ($e | ConvertTo-Json -Depth 10) -match "cEgo"
    }

    # ---- Generic properties (set then revert) ----
    Write-Host "Game data" -ForegroundColor Cyan
    Check "get_properties / set_properties round-trip on a character" {
        $readName = { ((Get-Json (Invoke-Mcp "get_properties" @{ type = "character"; id = "cEgo" })).properties | Where-Object { $_.name -eq "RealName" }).value }
        $before = & $readName
        Invoke-Mcp "set_properties" @{ type = "character"; id = "cEgo"; properties = @{ RealName = "SmokeTester" } } | Out-Null
        $after = & $readName
        Invoke-Mcp "set_properties" @{ type = "character"; id = "cEgo"; properties = @{ RealName = $before } } | Out-Null
        $after -eq "SmokeTester"
    }

    # ---- Scripts: compile error then fix ----
    Write-Host "Scripts" -ForegroundColor Cyan
    Check "create_script_module McpSmoke" {
        $r = Get-Json (Invoke-Mcp "create_script_module" @{ name = "McpSmoke" })
        $r -ne $null
    }
    Check "compile reports a parse error in bad code" {
        Invoke-Mcp "write_script" @{ name = "McpSmoke"; text = "int broken = ;" } | Out-Null
        $r = Get-Json (Invoke-Mcp "compile")
        $r.ok -eq $false -and (($r.messages | ConvertTo-Json -Depth 10) -match "McpSmoke")
    }
    Check "compile is clean after the fix" {
        Invoke-Mcp "write_script" @{ name = "McpSmoke"; text = "// fixed`r`nint ok_value = 1;" } | Out-Null
        $r = Get-Json (Invoke-Mcp "compile")
        $r.ok -eq $true
    }

    # ---- Rooms ----
    Write-Host "Rooms" -ForegroundColor Cyan
    Check "get_room(1) returns size and hotspots" {
        $r = Get-Json (Invoke-Mcp "get_room" @{ number = 1 })
        $r.size.width -eq 320 -and $r.hotspots.Count -gt 0
    }
    Check "render_room(1) returns a PNG image" { Has-Image (Invoke-Mcp "render_room" @{ number = 1 }) }
    Check "get_mask_pixel reads the walkable mask" {
        $r = Get-Json (Invoke-Mcp "get_mask_pixel" @{ number = 1; mask = "WalkableAreas"; x = 160; y = 140 })
        $r -ne $null
    }

    # a 4x4 opaque-ish PNG
    $png4 = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAFElEQVR4nGP8z8Dwn4EIwDiqEF0RACZvBAX2M0mGAAAAAElFTkSuQmCC"

    # ---- Room authoring (on a new room 2) ----
    Write-Host "Room authoring" -ForegroundColor Cyan
    Check "get_room(1) show=true opens the room tab" { (Get-Json (Invoke-Mcp "get_room" @{ number = 1; show = $true })).number -eq 1 }
    Check "create_room creates room 2" { (Get-Json (Invoke-Mcp "create_room" @{ number = 2; description = "Smoke room" })).created -eq $true }
    Check "set_room_background pads a small image to the room size" {
        $r = Get-Json (Invoke-Mcp "set_room_background" @{ number = 2; base64 = $png4 })
        $r.save.saved -and $r.size.width -eq 320
    }
    Check "draw_room_mask paints a walkable floor" {
        $shapes = @(@{ type = "polygon"; points = @(@(0, 150), @(319, 150), @(319, 199), @(0, 199)) })
        $r = Get-Json (Invoke-Mcp "draw_room_mask" @{ number = 2; mask = "walkableareas"; area = 1; clear = $true; shapes = $shapes })
        $r.save.saved -and ($r.paintedAreas -contains 1)
    }
    Check "draw_room_mask paints a hotspot and get_mask_pixel reads it" {
        Invoke-Mcp "draw_room_mask" @{ number = 2; mask = "hotspots"; area = 1; shapes = @(@{ type = "rect"; x = 100; y = 50; width = 40; height = 40 }) } | Out-Null
        (Get-Json (Invoke-Mcp "get_mask_pixel" @{ number = 2; mask = "hotspots"; x = 120; y = 70 })).area -eq 1
    }
    Check "set_room_properties names the hotspot" {
        $r = Get-Json (Invoke-Mcp "set_room_properties" @{ number = 2; entity = "hotspot:1"; properties = @{ Name = "hSmoke"; Description = "Smoke"; WalkToPoint = "120,160" } })
        $r.save.saved -and ($r.changed -contains "Name")
    }
    Check "create_room_object adds an object" {
        $r = Get-Json (Invoke-Mcp "create_room_object" @{ number = 2; properties = @{ Name = "oSmoke"; Image = 0; StartX = 200; StartY = 170 } })
        $r.created -and $r.name -eq "oSmoke"
    }
    Check "set_room_event binds hSmoke Look and adds a stub" {
        $r = Get-Json (Invoke-Mcp "set_room_event" @{ number = 2; entity = "hSmoke"; event = "Look" })
        $r.function -eq "hSmoke_Look" -and $r.save.saved
    }
    Check "room script is readable, editable and compiles" {
        $t = (Get-Json (Invoke-Mcp "read_script" @{ name = "room2" })).text
        Invoke-Mcp "edit_script" @{ name = "room2"; find = "function hSmoke_Look(Hotspot *theHotspot, CursorMode mode)`n{`n"; replace = "function hSmoke_Look(Hotspot *theHotspot, CursorMode mode)`n{`n  player.Say(`"smoke`");`n" } | Out-Null
        $c = Get-Json (Invoke-Mcp "compile" @{ name = "room2" })
        ($t -match "hSmoke_Look") -and $c.ok
    }
    Check "render_room(2) with a mask overlay names the areas" {
        $r = Invoke-Mcp "render_room" @{ number = 2; mask = "walkableareas" }
        (Has-Image $r) -and ((Get-Text $r) -match "1=")
    }
    Check "set_event binds a character event in GlobalScript" {
        (Get-Json (Invoke-Mcp "set_event" @{ type = "character"; id = "cEgo"; event = "Look" })).function -eq "cEgo_Look"
    }
    Check "dialog options round-trip" {
        $d = Get-Json (Invoke-Mcp "create_entity" @{ type = "dialog" })
        Invoke-Mcp "set_dialog_script" @{ id = $d.name; options = @(@{ text = "Hello" }, @{ text = "Bye"; show = $false }) } | Out-Null
        $g = Get-Json (Invoke-Mcp "get_dialog_script" @{ id = $d.name })
        $g.options.Count -eq 2 -and $g.options[1].show -eq $false
    }

    # ---- Assets ----
    Write-Host "Assets" -ForegroundColor Cyan
    $newSprite = $null
    Check "import_sprite then get_sprite returns a PNG" {
        $r = Get-Json (Invoke-Mcp "import_sprite" @{ base64 = $png4 })
        $script:newSprite = $r.number
        Has-Image (Invoke-Mcp "get_sprite" @{ number = $script:newSprite })
    }
    Check "create_view accepts the schema form ({frames:[{sprite, delay}]})" {
        $loops = @(@{ frames = @(@{ sprite = $script:newSprite; delay = 4 }, @{ sprite = $script:newSprite; flipped = $true }) })
        $v = Get-Json (Invoke-Mcp "create_view" @{ name = "vSmoke"; loops = $loops })
        $ok = $v.created -and $v.loops.Count -eq 1 -and $v.loops[0].frames.Count -eq 2 -and $v.loops[0].frames[1].flipped
        Invoke-Mcp "delete_entity" @{ type = "view"; id = "vSmoke" } | Out-Null
        $ok
    }

    # ---- Audio ----
    Write-Host "Audio" -ForegroundColor Cyan
    $wavPath = Join-Path $Folder "smoke_beep.wav"
    New-TestWav $wavPath 880 200
    $beep = $null
    $tone = $null
    Check "import_audio by path creates a Sound clip and its AudioCache copy" {
        $script:beep = Get-Json (Invoke-Mcp "import_audio" @{ path = $wavPath; name = "aSmokeBeep"; type = "Sound" })
        $script:beep.created -and $script:beep.typeName -eq "Sound" -and $script:beep.sourceFileName -eq "smoke_beep.wav" -and
            (Test-Path (Join-Path $Folder $script:beep.cacheFile))
    }
    Check "import_audio from base64 saves to Audio\ and derives the script name" {
        $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($wavPath))
        $script:tone = Get-Json (Invoke-Mcp "import_audio" @{ base64 = $b64; fileName = "smoke_tone.wav" })
        $script:tone.scriptName -eq "aSmoke_tone" -and $script:tone.index -eq ($script:beep.index + 1) -and
            (Test-Path (Join-Path $Folder "Audio\smoke_tone.wav"))
    }
    Check "list_entities and get_properties see the clip and its cache file" {
        $l = Get-Json (Invoke-Mcp "list_entities" @{ type = "audioclip" })
        $p = (Get-Json (Invoke-Mcp "get_properties" @{ type = "audioclip"; id = "aSmokeBeep" })).properties
        $cache = ($p | Where-Object { $_.name -eq "CacheFileNameWithoutPath" }).value
        (($l | ConvertTo-Json -Depth 10) -match "aSmoke_tone") -and $cache -eq (Split-Path -Leaf $script:beep.cacheFile)
    }
    Check "audiocliptype: set MaxChannels, create and delete a type" {
        $read = { ((Get-Json (Invoke-Mcp "get_properties" @{ type = "audiocliptype"; id = "Ambient Sound" })).properties | Where-Object { $_.name -eq "MaxChannels" }).value }
        $before = & $read
        Invoke-Mcp "set_properties" @{ type = "audiocliptype"; id = "Ambient Sound"; properties = @{ MaxChannels = 3 } } | Out-Null
        $after = & $read
        Invoke-Mcp "set_properties" @{ type = "audiocliptype"; id = "Ambient Sound"; properties = @{ MaxChannels = $before } } | Out-Null
        $t = Get-Json (Invoke-Mcp "create_entity" @{ type = "audiocliptype"; properties = @{ Name = "SmokeType" } })
        $d = Get-Json (Invoke-Mcp "delete_entity" @{ type = "audiocliptype"; id = "SmokeType" })
        $after -eq 3 -and $t.id -ne $null -and $d.deleted
    }
    Check "a script that plays the clip compiles" {
        Invoke-Mcp "write_script" @{ name = "McpSmoke"; text = "// fixed`r`nint ok_value = 1;`r`nfunction SmokePlay() { aSmokeBeep.Play(); }" } | Out-Null
        (Get-Json (Invoke-Mcp "compile")).ok -eq $true
    }
    Check "delete_audio refuses while a view frame or a script uses the clip" {
        $loops = @(@{ frames = @(@{ sprite = $script:newSprite; sound = $script:tone.index }) })
        Invoke-Mcp "create_view" @{ name = "vSmokeSnd"; loops = $loops } | Out-Null
        $refusedView = $false; $refusedScript = $false
        try { Invoke-Mcp "delete_audio" @{ clip = "aSmoke_tone" } | Out-Null } catch { $refusedView = $_.Exception.Message -match "vSmokeSnd" }
        try { Invoke-Mcp "delete_audio" @{ clip = "aSmokeBeep" } | Out-Null } catch { $refusedScript = $_.Exception.Message -match "McpSmoke" }
        $refusedView -and $refusedScript
    }
    Check "replace_audio re-copies the source and switches files, keeping the index" {
        $a = Get-Json (Invoke-Mcp "replace_audio" @{ clip = "aSmokeBeep" })
        $other = Join-Path $Folder "smoke_low.wav"
        New-TestWav $other 220 300
        $b = Get-Json (Invoke-Mcp "replace_audio" @{ clip = "aSmoke_tone"; path = $other })
        $a.replaced -and $b.index -eq $script:tone.index -and $b.sourceFileName -eq "smoke_low.wav" -and
            (Get-Item (Join-Path $Folder $b.cacheFile)).Length -eq (Get-Item $other).Length
    }
    Check "delete_audio force removes the clip and its cache copy" {
        $d = Get-Json (Invoke-Mcp "delete_audio" @{ clip = "aSmoke_tone"; force = $true })
        Invoke-Mcp "delete_entity" @{ type = "view"; id = "vSmokeSnd" } | Out-Null
        $d.deleted -and -not (Test-Path (Join-Path $Folder $script:tone.cacheFile))
    }

    Check "delete_sprite removes the imported sprite" {
        $r = Get-Json (Invoke-Mcp "delete_sprite" @{ number = $script:newSprite })
        $r -ne $null
    }

    # ---- Build ----
    Write-Host "Build" -ForegroundColor Cyan
    Check "build_game succeeds" { (Get-Json (Invoke-Mcp "build_game" @{} 300)).ok -eq $true }
    Check "the build kept the imported clip's AudioCache copy" { Test-Path (Join-Path $Folder $script:beep.cacheFile) }

    # ---- Run + player simulation ----
    Write-Host "Run and player simulation" -ForegroundColor Cyan
    Check "runtime_enable_plugin enables agsmcp" { (Get-Json (Invoke-Mcp "runtime_enable_plugin")).enabled -eq $true }
    Check "save_project persists" { (Get-Text (Invoke-Mcp "save_project")) -match "saved" }
    Check "run_game launches with the engine plugin" {
        $r = Get-Json (Invoke-Mcp "run_game" @{ startRoom = 1 } 300)
        $r.launched -eq $true
    }
    Start-Sleep -Seconds 4
    Check "game_state reports room 1 and the player" {
        $r = Get-Json (Invoke-Mcp "game_state")
        $r.ok -eq $true -and $r.room -eq 1 -and $r.player.scriptName -eq "cEgo"
    }
    Check "game_hover_name responds" { (Get-Json (Invoke-Mcp "game_hover_name" @{ x = 160; y = 120 })).ok -eq $true }
    Check "game_click responds" { (Get-Json (Invoke-Mcp "game_click" @{ x = 160; y = 120; button = "left" })).ok -eq $true }
    Check "game_process_click queues a walk and game_wait_until ready returns" {
        $c = Get-Json (Invoke-Mcp "game_process_click" @{ x = 200; y = 150; mode = 0 })
        $w = Get-Json (Invoke-Mcp "game_wait_until" @{ condition = "ready"; timeoutMs = 15000 })
        $c.queued -eq $true -and $w.met -eq $true
    }
    Check "game_screenshot returns a PNG" { Has-Image (Invoke-Mcp "game_screenshot") }
    Check "stop_game stops the game" { (Get-Json (Invoke-Mcp "stop_game")).stopped -eq $true }
}
finally {
    if ($Keep) { Write-Host "Left the smoke game open at $Folder (-Keep)." -ForegroundColor Yellow } else { Remove-SmokeGame }
}

Write-Host ""
Write-Host ("Smoke test: {0} passed, {1} failed." -f $script:pass, $script:fail) -ForegroundColor Cyan
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
