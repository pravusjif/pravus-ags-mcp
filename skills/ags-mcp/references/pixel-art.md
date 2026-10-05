# Generating pixel-art textures for AGS rooms

Low-resolution AGS games (320×200, 320×240, 640×400) want crisp pixel art: hard edges, a limited palette, ordered dither for gradients, no anti-aliasing. Generating it with Python/PIL is fast, reproducible and gives exact control over every pixel; 3D tools (Blender) produce anti-aliased renders that need heavy cleanup and are rarely worth it at this size.

`scripts/pixelart.py` (next to this skill) has the helpers used below. Copy it into the game's `Art/` folder or import it from the skill path.

## Rules that keep it looking like pixel art

- Draw at native resolution. Never draw big and downscale (that blurs); never upscale with anything but nearest-neighbour.
- PIL's `ImageDraw` primitives are aliased by default, which is what you want. Avoid `Image.resize` with the default filter and avoid `ImageFilter` blurs.
- Gradients: `dither(img, box, colour_a, colour_b)` (4×4 Bayer). Skies, walls, floor shading.
- Volume: light edge top/left (`shade(c, +20..+40)`), dark edge bottom/right (`shade(c, -25..-40)`), one or two steps. A 1-px highlight line sells wood, metal and glass.
- Texture: `rect(img, …, c, jitter=3..5)` for per-pixel noise on plaster, wood, stone; a few darker "grain" dashes on planks; occasional knots.
- Shadows: `shadow_ellipse` or `blend_px` with low alpha in 2–4 stepped bands under furniture and along the wall/floor join. Stepped, not smooth.
- Palette: pick 2–4 base colours per material and derive everything with `shade`/`mix`. A room with warm wood + one accent colour reads well.
- Seed the RNG (`random.Random(1337)`) so re-running the script reproduces the same textures.

## Backgrounds

- Exactly the game resolution (`project_info`), RGB, saved as PNG. `set_room_background` resizes the room and **clears all masks** if the size differs, so get the size right before painting masks.
- Lay the room out in bands: ceiling/upper wall, wainscot or horizon, baseboard, floor (planks that grow taller toward the viewer sell depth). The walkable floor should be a clean band you can describe with one polygon.
- Draw static furniture into the background. Only things that change or need per-pixel clicks are sprites (see below).
- Bake a subtle light spill (window, fire) with a low-alpha polygon so a light-source sprite has something to sit on.

## Sprites (objects, inventory, animation frames)

- RGBA with a fully transparent background, tightly cropped. `import_sprite` keeps the alpha in 32-bit games.
- Interactive objects need solid pixels where the player will click: AGS hit-tests objects per pixel.
- Two-state objects (closed/open, cold/lit) are two sprites of the same size with the same origin, so `StartX/StartY` stay valid when you swap `Graphic`.
- Inventory icons: ~24×16 or 32×24 with a 1-px outline; make a smaller copy with `Image.NEAREST` for the in-room pickup if it must be small.
- Animation: draw frames with a per-frame seeded RNG (`random.Random(100 + f*17)`) so flames/flickers vary but reproduce. 4 frames at delay 4–6 is plenty.

## Preview before importing

```python
from pixelart import preview
preview("roomN_bg.png", [("chest_closed.png", 86, 120),     # (file, x, y_bottom): the planned StartX/StartY
                         ("torch_0.png", 162, 114)],
        scale=3, out="preview.png")
```

Open the preview (Read the PNG) and check the composition, the scale of props against the player (look at the player character's walking sprite with `get_sprite` to get its height), and whether the stand points you planned leave the objects visible. Fix in the generator, re-run, then import. After import, `render_room` with and without masks is the ground truth.

## Template for a room generator

```python
import random
from pathlib import Path
from PIL import Image, ImageDraw
from pixelart import new, rect, dither, shade, mix, jit, put, blend_px, shadow_ellipse, preview

OUT = Path(__file__).parent / "roomN"; OUT.mkdir(exist_ok=True)
R = random.Random(1337)
W, H = 320, 200                                                 # the game's resolution (project_info)

def draw_room():
    img = new(W, H, (0, 0, 0, 255))
    dither(img, 0, 0, W-1, 79, (214,188,148), (190,160,120))   # wall
    rect(img, 0, 80, W-1, 117, (120,78,46), j=4)                # wainscot
    rect(img, 0, 118, W-1, H-1, (150,104,62), j=3)              # floor
    return img

def draw_prop():
    img = new(32, 40)                                           # transparent
    rect(img, 2, 4, 29, 39, (140,92,52), j=4)
    rect(img, 2, 4, 29, 4, shade((140,92,52), 36))
    return img

bg = draw_room(); bg.convert("RGB").save(OUT / "roomN_bg.png")
draw_prop().save(OUT / "prop.png")
preview(OUT / "roomN_bg.png", [(OUT / "prop.png", 100, 150)], scale=3, out=OUT / "preview.png")
```

Keep the generator in the game folder (`Art/roomN_art.py`) with its output folder, so textures can be regenerated and tweaked later; `replace_sprite` keeps sprite numbers stable while you iterate.
