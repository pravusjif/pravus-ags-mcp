<#
.SYNOPSIS
  End-to-end smoke test: drive a live AGS editor (with the MCP plugin) through every tool group
  over HTTP, on a throwaway game it creates itself.

.DESCRIPTION
  Requires the AGS editor running with the MCP server up and a game open (build.ps1 -Engine -Deploy -Run
  opens -Game <Game.agf>, else the smoke game, see tools/SmokeGame.ps1). The engine plugin (agsmcp.dll) must be
  deployed next to AGSEditor.exe so runtime_enable_plugin can find it.

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
    Check "batch runs project_info and list_rooms in one call" {
        $r = Get-Json (Invoke-Mcp "batch" @{ calls = @(@{ name = "project_info" }, @{ name = "list_rooms" }) })
        -not $r.stopped -and $r.results.Count -eq 2 -and $r.results[0].ok -and $r.results[0].content[0].gameName -eq "MCP Smoke" -and $r.results[1].ok
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
        $r.ok -eq $true -and $r.staleOnDisk.Count -eq 0
    }
    Check "a disk edit shows as staleOnDisk in read_script and compile" {
        Set-Content (Join-Path $Folder "McpSmoke.asc") "// edited on disk`r`nint ok_value = 2;"
        $r = Get-Json (Invoke-Mcp "read_script" @{ name = "McpSmoke" })
        $c = Get-Json (Invoke-Mcp "compile" @{ name = "McpSmoke" })
        $r.staleOnDisk -eq $true -and $r.text -match "fixed" -and ($c.staleOnDisk -contains "McpSmoke.asc")
    }
    Check "move_script_module moves McpSmoke to the top and list_scripts follows" {
        $m = Get-Json (Invoke-Mcp "move_script_module" @{ name = "McpSmoke"; direction = "index"; index = 0 })
        $l = Get-Json (Invoke-Mcp "list_scripts")
        $refused = $false
        try { Invoke-Mcp "move_script_module" @{ name = "GlobalScript"; direction = "up" } | Out-Null } catch { $refused = $_.Exception.Message -match "cannot be moved" }
        $m.to -eq 0 -and $m.moved -and $l.modules[0].name -eq "McpSmoke" -and $refused
    }
    Check "reload_script takes the disk version and clears staleOnDisk" {
        $l = Get-Json (Invoke-Mcp "reload_script" @{ name = "McpSmoke" })
        $r = Get-Json (Invoke-Mcp "read_script" @{ name = "McpSmoke" })
        $disk = (Get-Content (Join-Path $Folder "McpSmoke.asc")) -join "`n"
        ($l.files | Where-Object { $_.file -eq "McpSmoke.asc" }).changed -eq $true -and
            $r.staleOnDisk -eq $false -and $r.text -eq $disk
    }

    # ---- Rooms ----
    Write-Host "Rooms" -ForegroundColor Cyan
    Check "get_room(1) returns size and hotspots" {
        $r = Get-Json (Invoke-Mcp "get_room" @{ number = 1 })
        $r.size.width -eq 320 -and $r.hotspots.Count -gt 0
    }
    Check "render_room(1) returns a PNG image" { Has-Image (Invoke-Mcp "render_room" @{ number = 1 }) }
    Check "render_room and get_sprite outPath write PNG files and return text only" {
        $room = Join-Path $Folder "out\room1.png"; $sprite = Join-Path $Folder "out\sprite0.png"
        $r = Invoke-Mcp "render_room" @{ number = 1; outPath = $room }
        $s = Invoke-Mcp "get_sprite" @{ number = 0; outPath = $sprite }
        $j = Get-Json $r
        -not (Has-Image $r) -and -not (Has-Image $s) -and (Get-Item $room).Length -gt 0 -and (Get-Item $sprite).Length -gt 0 -and
            $j.width -gt 0 -and $j.path -eq $room
    }
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
        $r = Get-Json (Invoke-Mcp "create_room_object" @{ number = 2; properties = @{ Name = "oSmoke"; Description = "Smoke pot"; Image = 0; StartX = 200; StartY = 170 } })
        $r.created -and $r.name -eq "oSmoke"
    }
    Check "set_room_event binds hSmoke Look and adds a stub" {
        $r = Get-Json (Invoke-Mcp "set_room_event" @{ number = 2; entity = "hSmoke"; event = "Look" })
        $r.function -eq "hSmoke_Look" -and $r.save.saved
    }
    Check "room script is readable, editable and compiles" {
        $t = (Get-Json (Invoke-Mcp "read_script" @{ name = "room2" })).text
        # MCP_RoomPing is for game_call_function roomScript=true; MCPLOOK shows game_run_interaction ran the Look event.
        Invoke-Mcp "edit_script" @{ name = "room2"; find = "function hSmoke_Look(Hotspot *theHotspot, CursorMode mode)`n{`n"; replace = "function MCP_RoomPing()`n{`n  System.Log(eLogInfo, `"MCPROOM`");`n}`n`nfunction hSmoke_Look(Hotspot *theHotspot, CursorMode mode)`n{`n  System.Log(eLogInfo, `"MCPLOOK`");`n  player.Say(`"smoke`");`n" } | Out-Null
        $c = Get-Json (Invoke-Mcp "compile" @{ name = "room2" })
        ($t -match "hSmoke_Look") -and $c.ok
    }
    Check "reload_script reloads a room script edited on disk" {
        Add-Content (Join-Path $Folder "room2.asc") "// reloaded from disk"
        $before = (Get-Json (Invoke-Mcp "read_script" @{ name = "room2" })).staleOnDisk
        Invoke-Mcp "reload_script" @{ name = "room2" } | Out-Null
        $r = Get-Json (Invoke-Mcp "read_script" @{ name = "room2" })
        $before -eq $true -and $r.staleOnDisk -eq $false -and $r.text -match "reloaded from disk"
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
        $script:smokeDialog = $d.name
        $g.options.Count -eq 2 -and $g.options[1].show -eq $false
    }
    Check "set_dialog_script stores an LF script with CRLF" {
        Invoke-Mcp "set_dialog_script" @{ id = $script:smokeDialog; script = "@S`nreturn`n@1`nnarrator: Hi`nreturn`n" } | Out-Null
        $s = (Get-Json (Invoke-Mcp "get_dialog_script" @{ id = $script:smokeDialog })).script
        ($s -match "narrator: Hi\r\n") -and -not ($s -match "[^\r]\n")
    }
    Check "list_strings lists the smoke dialog's options and its speech line" {
        $o = Get-Json (Invoke-Mcp "list_strings" @{ type = "DialogOption"; limit = 1000 })
        $mine = @($o.strings | Where-Object { $_.source -like "dialog $($script:smokeDialog) option *" })
        $d = Get-Json (Invoke-Mcp "list_strings" @{ type = "DialogScript"; contains = "Hi" })
        $line = $d.strings | Where-Object { $_.source -eq "dialog $($script:smokeDialog)" -and $_.text -eq "Hi" }
        $mine.Count -eq 2 -and ($mine.text -contains "Hello") -and ($mine.text -contains "Bye") -and
            -not ($o.strings | Where-Object { $_.type -ne "DialogOption" }) -and $line.speaker -eq "narrator" -and $line.line -eq 4
    }
    Check "list_strings rooms=true adds room text and reopens the loaded room" {
        $all = Get-Json (Invoke-Mcp "list_strings" @{ rooms = $true; limit = 1 })
        $page = Get-Json (Invoke-Mcp "list_strings" @{ limit = 2; offset = 1 })
        $all.total -gt 0 -and $all.count -eq 1 -and $all.truncated -and $page.offset -eq 1 -and $page.count -eq 2
    }

    # ---- Text measurement ----
    Write-Host "Text" -ForegroundColor Cyan
    Check "measure_text measures and wraps a line in font 0" {
        $one = Get-Json (Invoke-Mcp "measure_text" @{ font = "0"; text = "Hello" })
        $two = Get-Json (Invoke-Mcp "measure_text" @{ font = "0"; text = "Hello[Hello" })
        $hh = Get-Json (Invoke-Mcp "measure_text" @{ font = "0"; text = "Hello Hello" })
        # The engine wraps at maxWidth - 1, so maxWidth = width + 1 is the tightest fit for "Hello Hello".
        $wrap = Get-Json (Invoke-Mcp "measure_text" @{ font = "0"; text = "Hello Hello Hello"; maxWidth = $hh.width + 1 })
        $one.width -gt 0 -and $one.lineCount -eq 1 -and $one.fontHeight -gt 0 -and
            $two.lineCount -eq 2 -and $two.lines[1].width -eq $one.width -and $two.height -eq $one.lineSpacing + $one.height -and
            $wrap.lineCount -eq 2 -and $wrap.lines[0].text -eq "Hello Hello" -and $wrap.lines[1].text -eq "Hello" -and $wrap.width -eq $hh.width
    }

    # ---- GUI controls ----
    Write-Host "GUI controls" -ForegroundColor Cyan
    Check "create_gui_control adds a button to a template GUI and list_gui_controls shows it" {
        $guis = (Get-Json (Invoke-Mcp "list_entities" @{ type = "gui" })).entities
        foreach ($g in $guis) {
            $l = Get-Json (Invoke-Mcp "list_gui_controls" @{ gui = $g.name })
            if ($l.guiType -eq "NormalGUI") { $script:smokeGui = $l; break }
        }
        $c = Get-Json (Invoke-Mcp "create_gui_control" @{ gui = $script:smokeGui.gui; type = "button"; x = 4; y = 4; properties = @{ Name = "btnSmoke" } })
        $script:smokeCtrl = $c
        $l = Get-Json (Invoke-Mcp "list_gui_controls" @{ gui = $script:smokeGui.gui })
        $c.controlName -eq "btnSmoke" -and $c.controlId -eq $script:smokeGui.count -and
            ($l.controls | Where-Object { $_.name -eq "btnSmoke" -and $_.type -eq "Button" -and $_.width -eq 40 })
    }
    Check "set_properties and get_properties work on a guicontrol" {
        Invoke-Mcp "set_properties" @{ type = "guicontrol"; id = "$($script:smokeGui.gui):btnSmoke"; properties = @{ Text = "Smoke!" } } | Out-Null
        $p = Get-Json (Invoke-Mcp "get_properties" @{ type = "guicontrol"; id = $script:smokeCtrl.id })
        ($p.properties | Where-Object { $_.name -eq "Text" }).value -eq "Smoke!" -and $p.name -eq "$($script:smokeGui.gui):btnSmoke"
    }
    Check "set_event guicontrol and gui add OnClick stubs to GlobalScript and compile" {
        $e = Get-Json (Invoke-Mcp "set_event" @{ type = "guicontrol"; id = "$($script:smokeGui.gui):btnSmoke"; event = "OnClick" })
        $g = Get-Json (Invoke-Mcp "set_event" @{ type = "gui"; id = $script:smokeGui.gui; event = "Click"; function = "SmokeGui_OnClick" })
        $text = (Get-Json (Invoke-Mcp "read_script" @{ name = "GlobalScript" })).text
        $c = Get-Json (Invoke-Mcp "compile")
        $e.function -eq "btnSmoke_OnClick" -and $e.parameters -eq "GUIControl *control, MouseButton button" -and
            $text -match "function btnSmoke_OnClick\(GUIControl \*control, MouseButton button\)" -and
            $text -match "function SmokeGui_OnClick\(GUI \*theGui, MouseButton button\)" -and $c.ok
    }
    Check "delete_gui_control removes the button" {
        $d = Get-Json (Invoke-Mcp "delete_gui_control" @{ gui = $script:smokeGui.gui; control = "btnSmoke" })
        $l = Get-Json (Invoke-Mcp "list_gui_controls" @{ gui = $script:smokeGui.gui })
        $d.deleted -and -not ($l.controls | Where-Object { $_.name -eq "btnSmoke" }) -and $l.count -eq $script:smokeGui.count
    }
    # ---- Assets ----
    Write-Host "Assets" -ForegroundColor Cyan
    $newSprite = $null
    Check "import_sprite then get_sprite returns a PNG" {
        $r = Get-Json (Invoke-Mcp "import_sprite" @{ base64 = $png4 })
        $script:newSprite = $r.number
        Has-Image (Invoke-Mcp "get_sprite" @{ number = $script:newSprite })
    }
    Check "create_sprite_folder, import_sprite into it, list_sprites shows it there" {
        $folder = (Get-Json (Invoke-Mcp "create_sprite_folder" @{ name = "SmokeFolder" })).name
        $again = (Get-Json (Invoke-Mcp "create_sprite_folder" @{ name = "SmokeFolder"; parent = $folder })).name
        $n = (Get-Json (Invoke-Mcp "import_sprite" @{ base64 = $png4; folder = $folder })).number
        $tree = Get-Json (Invoke-Mcp "list_sprites" @{ folder = $folder })
        $flat = Get-Json (Invoke-Mcp "list_sprites" @{ flat = $true })
        $root = Get-Json (Invoke-Mcp "list_sprites")
        $tree.name -eq $folder -and $again -eq "${folder}1" -and ($tree.sprites | Where-Object { $_.number -eq $n -and $_.width -eq 4 }) -and
            $tree.folders[0].name -eq $again -and ($flat.sprites.number -contains $n) -and
            ($root.folders.name -contains $folder) -and -not ($root.sprites.number -contains $n)
    }
    $newFont = $null
    function Get-FontProps { param($Id) $h = @{}; (Get-Json (Invoke-Mcp "get_properties" @{ type = "font"; id = $Id })).properties | ForEach-Object { $h[$_.name] = $_.value }; $h }
    Check "create_entity font copies font 0 into a new slot and its agsfnt file" {
        $before = (Get-Json (Invoke-Mcp "list_entities" @{ type = "font" })).count
        $r = Get-Json (Invoke-Mcp "create_entity" @{ type = "font"; properties = @{ CopyFrom = 0; LineSpacing = 3 } })
        $script:newFont = [int]$r.id
        $after = (Get-Json (Invoke-Mcp "list_entities" @{ type = "font" })).count
        $p = Get-FontProps $script:newFont
        $after -eq $before + 1 -and $script:newFont -eq $before -and $r.applied -contains "LineSpacing" -and -not $r.ignored -and
            (Test-Path (Join-Path $Folder "agsfnt$($script:newFont).wfn")) -and $p.ProjectFilename -eq "agsfnt$($script:newFont).wfn" -and $p.Height -gt 0
    }
    Check "import_font puts a TTF and then a WFN over the slot and refuses other files" {
        $n = $script:newFont
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "ags-mcp-smoke-font"
        New-Item -ItemType Directory -Force $tmp | Out-Null
        $wfnCopy = Join-Path $tmp "smoke.wfn"
        Copy-Item (Join-Path $Folder "agsfnt0.wfn") $wfnCopy -Force
        $bad = $false
        try { Invoke-Mcp "import_font" @{ id = $n; path = (Join-Path $tmp "smoke.otf") } | Out-Null } catch { $bad = $_.Exception.Message -match "\.ttf and \.wfn" }
        $ttfOk = $true
        $ttf = Join-Path $env:WINDIR "Fonts\arial.ttf"
        if (Test-Path $ttf) {
            $t = Get-Json (Invoke-Mcp "import_font" @{ id = $n; path = $ttf; height = 14 })
            $ttfOk = $t.projectFile -eq "agsfnt$n.ttf" -and $t.pointSize -gt 0 -and $t.height -gt 0 -and
                (Test-Path (Join-Path $Folder "agsfnt$n.ttf")) -and -not (Test-Path (Join-Path $Folder "agsfnt$n.wfn"))
        } else { Write-Host "    (no $ttf; TTF import not exercised)" -ForegroundColor DarkGray }
        $w = Get-Json (Invoke-Mcp "import_font" @{ id = $n; path = $wfnCopy })
        $p = Get-FontProps $n
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        $bad -and $ttfOk -and $w.projectFile -eq "agsfnt$n.wfn" -and $w.pointSize -eq 0 -and $w.height -gt 0 -and
            $p.ProjectFilename -eq "agsfnt$n.wfn" -and $p.SourceFilename -eq $wfnCopy -and -not (Test-Path (Join-Path $Folder "agsfnt$n.ttf"))
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
    Check "audiofolder: create with defaults, import into it, refuse deleting it while non-empty" {
        $f = Get-Json (Invoke-Mcp "create_entity" @{ type = "audiofolder"; properties = @{ Name = "SmokeSfx"; DefaultType = "Sound"; DefaultVolume = 60 } })
        $vol = ((Get-Json (Invoke-Mcp "get_properties" @{ type = "audiofolder"; id = "SmokeSfx" })).properties | Where-Object { $_.name -eq "DefaultVolume" }).value
        $c = Get-Json (Invoke-Mcp "import_audio" @{ path = $wavPath; name = "aSmokeSfx"; folder = "SmokeSfx" })
        $refused = $false
        try { Invoke-Mcp "delete_entity" @{ type = "audiofolder"; id = "SmokeSfx" } | Out-Null } catch { $refused = $_.Exception.Message -match "not empty" }
        Invoke-Mcp "delete_audio" @{ clip = "aSmokeSfx" } | Out-Null
        $d = Get-Json (Invoke-Mcp "delete_entity" @{ type = "audiofolder"; id = "SmokeSfx" })
        $f.created -and $vol -eq 60 -and $c.folder -eq "SmokeSfx" -and $c.typeName -eq "Sound" -and $refused -and $d.deleted
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

    # Hooks for the run checks: log every key and text character, and a function that quits the game.
    Check "GlobalScript gets key/text logging and MCP_Quit, and compiles" {
        $t = (Get-Json (Invoke-Mcp "read_script" @{ name = "GlobalScript" })).text
        $hooked = [regex]::Replace($t, '(function on_key_press\([^)]*\)\s*\{)', "`$1`r`n  System.Log(eLogInfo, `"MCPKEY %d`", keycode);", 1)
        $hooked += "`r`nfunction on_text_input(int ch)`r`n{`r`n  System.Log(eLogInfo, `"MCPTXT %d`", ch);`r`n}`r`n" +
                   "`r`nfunction MCP_Quit()`r`n{`r`n  QuitGame(0);`r`n}`r`n" +
                   "`r`nfunction MCP_Room(int n)`r`n{`r`n  player.ChangeRoom(n);`r`n}`r`n" +
                   "`r`nfunction MCP_HidePopups()`r`n{`r`n  for (int i = 0; i < Game.GUICount; i++)`r`n  {`r`n    if (gui[i].PopupStyle == eGUIPopupModal) gui[i].Visible = false;`r`n  }`r`n}`r`n"
        Invoke-Mcp "write_script" @{ name = "GlobalScript"; text = $hooked } | Out-Null
        $c = Get-Json (Invoke-Mcp "compile")
        $hooked -ne $t -and $c.ok -eq $true
    }
    # Log lines since the given 0-based line.
    function Get-GameLogSince { param([int]$Since) Get-Json (Invoke-Mcp "get_game_log" @{ sinceLine = $Since; limit = 5000 }) }

    # ---- Build ----
    Write-Host "Build" -ForegroundColor Cyan
    Check "build_game succeeds" { (Get-Json (Invoke-Mcp "build_game" @{} 300)).ok -eq $true }
    Check "the build kept the imported clip's AudioCache copy" { Test-Path (Join-Path $Folder $script:beep.cacheFile) }
    $windowsDir = Join-Path $Folder "Compiled\Windows"
    Check "build_game mode=release drops the plugin and Debug mode" {
        $b = Get-Json (Invoke-Mcp "build_game" @{ mode = "release" } 300)
        $info = Get-Json (Invoke-Mcp "project_info")
        $b.ok -eq $true -and -not (Test-Path (Join-Path $windowsDir "agsmcp.dll")) -and
            $info.debugMode -eq $false -and @($info.plugins).Count -eq 0 -and $info.enginePlugin.enabled -eq $false
    }
    Check "build_game mode=debug restores Debug mode and the plugin" {
        $b = Get-Json (Invoke-Mcp "build_game" @{ mode = "debug" } 300)
        $info = Get-Json (Invoke-Mcp "project_info")
        $b.ok -eq $true -and (Test-Path (Join-Path $windowsDir "agsmcp.dll")) -and
            $info.debugMode -eq $true -and @($info.plugins) -contains "agsmcp.dll" -and $info.enginePlugin.enabled -eq $true
    }

    # ---- Run + player simulation ----
    Write-Host "Run and player simulation" -ForegroundColor Cyan
    Check "runtime_disable_plugin disables agsmcp" {
        $d = Get-Json (Invoke-Mcp "runtime_disable_plugin")
        $info = Get-Json (Invoke-Mcp "project_info")
        $d.enabled -eq $false -and $info.enginePlugin.enabled -eq $false -and -not (@($info.plugins) -contains "agsmcp.dll")
    }
    Check "runtime_enable_plugin enables agsmcp" { (Get-Json (Invoke-Mcp "runtime_enable_plugin")).enabled -eq $true }
    Check "save_project persists" { (Get-Text (Invoke-Mcp "save_project")) -match "saved" }
    Check "run_game launches with the engine plugin" {
        $r = Get-Json (Invoke-Mcp "run_game" @{ startRoom = 1 } 300)
        $script:firstPid = $r.pid
        $r.launched -eq $true
    }
    Check "run_game again while the game runs relaunches cleanly" {
        $r = Get-Json (Invoke-Mcp "run_game" @{ startRoom = 1 } 300)
        $s = Get-Json (Invoke-Mcp "game_status")
        $r.launched -eq $true -and $r.pid -ne $script:firstPid -and $s.running -eq $true -and $s.pid -eq $r.pid
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
    Check "game_screenshot outPath writes the PNG" {
        $shot = Join-Path $Folder "out\shot.png"
        $j = Get-Json (Invoke-Mcp "game_screenshot" @{ outPath = $shot })
        (Get-Item $shot).Length -gt 0 -and $j.width -gt 0
    }
    Check "game_screenshot burst of 5 frames every 2 writes 5 PNGs and frames.json" {
        $dir = Join-Path $Folder "out\burst"
        $b = Get-Json (Invoke-Mcp "game_screenshot" @{ outPath = $dir; frames = 5; everyN = 2 } 120)
        $pngs = @(Get-ChildItem $dir -Filter "frame-*.png")
        $meta = Get-Content (Join-Path $dir "frames.json") -Raw | ConvertFrom-Json
        $ms = @($meta.frames | ForEach-Object { $_.ms })
        $ordered = ($ms -join ",") -eq (($ms | Sort-Object) -join ",")
        Write-Host ("    {0} frames over {1} ms: {2}" -f $b.count, $b.elapsedMs, ($ms -join ",")) -ForegroundColor DarkGray
        $b.count -eq 5 -and $pngs.Count -eq 5 -and @($meta.frames).Count -eq 5 -and $ordered -and $ms[4] -gt 0 -and
            @(Get-ChildItem (Join-Path $env:TEMP "ags-mcp-run") -Recurse -Filter "agsmcp*.bmp" -ErrorAction SilentlyContinue).Count -eq 0
    }
    Check "game_screenshot refuses a burst without outPath" {
        try { Invoke-Mcp "game_screenshot" @{ frames = 3 } | Out-Null; $false } catch { $_.Exception.Message -match "outPath" }
    }
    Check "game_key Up and F7 reach on_key_press as 372 and 365 (window-routed)" {
        $since = (Get-GameLogSince 0).nextLine
        $up = Get-Json (Invoke-Mcp "game_key" @{ key = "Up" })
        $f7 = Get-Json (Invoke-Mcp "game_key" @{ key = "F7" })
        Start-Sleep -Milliseconds 800
        $lines = (Get-GameLogSince $since).lines
        $up.via -eq "window" -and $f7.via -eq "window" -and
            ($lines -match "MCPKEY 372") -and ($lines -match "MCPKEY 365") -and -not ($lines -match "MCPKEY 56")
    }
    Check "game_wait_until logLine finds the MCPKEY line a game_key writes" {
        $since = (Get-GameLogSince 0).nextLine
        Invoke-Mcp "game_key" @{ key = "B" } | Out-Null
        $w = Get-Json (Invoke-Mcp "game_wait_until" @{ condition = "logLine"; pattern = "MCPKEY 66"; sinceLine = $since; timeoutMs = 5000 })
        Write-Host ("    line {0} after {1} ms: {2}" -f $w.lineIndex, $w.elapsedMs, $w.line) -ForegroundColor DarkGray
        $w.met -eq $true -and $w.line -match "MCPKEY 66" -and $w.lineIndex -ge $since -and $w.nextLine -eq $w.lineIndex + 1
    }
    Check "game_wait_until logLine times out on a line that never comes" {
        $w = Get-Json (Invoke-Mcp "game_wait_until" @{ condition = "logLine"; pattern = "NEVER_WRITTEN_\d+"; timeoutMs = 600 })
        $w.met -eq $false -and $w.elapsedMs -ge 600 -and $w.lineIndex -eq $null
    }
    Check "game_type sends text input that reaches on_text_input" {
        $since = (Get-GameLogSince 0).nextLine
        $t = Get-Json (Invoke-Mcp "game_type" @{ text = "Hi" })
        Start-Sleep -Milliseconds 800
        $lines = (Get-GameLogSince $since).lines
        $t.units -eq 2 -and ($lines -match "MCPTXT 72") -and ($lines -match "MCPTXT 105")
    }
    Check "game_text_extent measures 'Hi' in font 0 as measure_text does" {
        $e = Get-Json (Invoke-Mcp "game_text_extent" @{ font = 0; text = "Hi" })
        $m = Get-Json (Invoke-Mcp "measure_text" @{ font = 0; text = "Hi" })
        Write-Host ("    engine {0}x{1}, measure_text width {2}" -f $e.width, $e.height, $m.width) -ForegroundColor DarkGray
        $e.width -gt 0 -and $e.height -gt 0 -and $e.width -eq $m.width
    }
    # The template's F7 opens its restore dialog, a modal popup that pauses the game and covers the room.
    Check "game_call_function MCP_HidePopups closes the dialog F7 opened and unpauses the game" {
        Invoke-Mcp "game_call_function" @{ name = "MCP_HidePopups" } | Out-Null
        Start-Sleep -Milliseconds 500
        (Get-Json (Invoke-Mcp "game_state")).paused -eq 0
    }
    Check "game_call_function MCP_Room(2) takes the player to room 2" {
        Invoke-Mcp "game_call_function" @{ name = "MCP_Room"; args = @(2) } | Out-Null
        (Get-Json (Invoke-Mcp "game_wait_until" @{ condition = "room"; room = 2; timeoutMs = 15000 })).met -eq $true
    }
    Check "game_call_function roomScript=true runs a room2.asc function" {
        $since = (Get-GameLogSince 0).nextLine
        $r = Get-Json (Invoke-Mcp "game_call_function" @{ name = "MCP_RoomPing"; roomScript = $true })
        Start-Sleep -Milliseconds 800
        $lines = (Get-GameLogSince $since).lines
        $r.queued -eq $true -and $r.globalScript -eq $false -and $r.room -eq 2 -and ($lines -match "MCPROOM")
    }
    Check "game_call_function roomScript=true refuses a GlobalScript-only function" {
        try { Invoke-Mcp "game_call_function" @{ name = "MCP_Quit"; roomScript = $true } | Out-Null; $false }
        catch { $_.Exception.Message -match "room2.asc defines no function" }
    }
    Check "game_run_interaction hotspot:hSmoke mode=1 runs its Look event and the game gets ready" {
        $since = (Get-GameLogSince 0).nextLine
        $r = Get-Json (Invoke-Mcp "game_run_interaction" @{ target = "hotspot:hSmoke"; mode = 1 } 120)
        $w = Get-Json (Invoke-Mcp "game_wait_until" @{ condition = "ready"; timeoutMs = 20000 })
        Write-Host ("    point {0},{1} after {2} tried" -f $r.point.x, $r.point.y, $r.tried) -ForegroundColor DarkGray
        $r.hoverName -eq "Smoke" -and $r.queued -eq $true -and $w.met -eq $true -and ((Get-GameLogSince $since).lines -match "MCPLOOK")
    }
    Check "game_run_interaction object:oSmoke and character:cEgo (Walk) find their sprites" {
        $o = Get-Json (Invoke-Mcp "game_run_interaction" @{ target = "object:oSmoke"; mode = 0 } 120)
        $w1 = Get-Json (Invoke-Mcp "game_wait_until" @{ condition = "ready"; timeoutMs = 20000 })
        $c = Get-Json (Invoke-Mcp "game_run_interaction" @{ target = "character:cEgo"; mode = 0 } 120)
        $w2 = Get-Json (Invoke-Mcp "game_wait_until" @{ condition = "ready"; timeoutMs = 20000 })
        Write-Host ("    object at {0},{1} ({2} tried), character at {3},{4} ({5} tried)" -f $o.point.x, $o.point.y, $o.tried, $c.point.x, $c.point.y, $c.tried) -ForegroundColor DarkGray
        $o.hoverName -eq "Smoke pot" -and $o.queued -and $w1.met -and $c.hoverName -eq $w1.state.player.name -and $c.queued -and $w2.met
    }
    Check "game_run_interaction refuses a missing hotspot" {
        try { Invoke-Mcp "game_run_interaction" @{ target = "hotspot:hNoSuch"; mode = 1 } 120 | Out-Null; $false }
        catch { $_.Exception.Message -match "no hotspot 'hNoSuch'" }
    }
    Check "stop_game stops the game" { (Get-Json (Invoke-Mcp "stop_game")).stopped -eq $true }
    Check "a game that calls QuitGame exits within 3 s while the bridge is connected" {
        $r = Get-Json (Invoke-Mcp "run_game" @{ startRoom = 1 } 300)
        Start-Sleep -Seconds 4
        Invoke-Mcp "game_state" | Out-Null # the bridge now holds an open connection
        Invoke-Mcp "game_call_function" @{ name = "MCP_Quit" } | Out-Null
        $sw = [Diagnostics.Stopwatch]::StartNew()
        do { Start-Sleep -Milliseconds 200; $s = Get-Json (Invoke-Mcp "game_status") } while ($s.running -and $sw.ElapsedMilliseconds -lt 10000)
        $sw.Stop()
        Write-Host ("    exited after {0} ms" -f $sw.ElapsedMilliseconds) -ForegroundColor DarkGray
        $r.launched -and -not $s.running -and $sw.ElapsedMilliseconds -lt 3000
    }
}
finally {
    if ($Keep) { Write-Host "Left the smoke game open at $Folder (-Keep)." -ForegroundColor Yellow } else { Remove-SmokeGame }
}

Write-Host ""
Write-Host ("Smoke test: {0} passed, {1} failed." -f $script:pass, $script:fail) -ForegroundColor Cyan
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
