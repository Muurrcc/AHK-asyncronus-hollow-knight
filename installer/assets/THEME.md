# Asyncronus — installer visual theme

Direction: silent darkness, mist and bone. Sober, lots of empty space, a single point of light (the "soul glint"). No Team Cherry artwork or logos: the mask is an original shape (an oval with two uneven points and slanted slit eyes) and its offset blue echo stands for the second player / asynchrony.

## Palette

| Role | Hex |
|---|---|
| Mist | `#8FA3B5` |
| Paper | `#ECE8DC` |
| Soul glint (only accent) | `#CFE8F5` |
| Ink (titles) | `#344252` |

Contrast on Paper: body text ~14:1, Ink ~8:1, Mist ~7:1.

## Images (WixUI)

- `banner.bmp` (493×58, 24-bit): light banner; the mask sits at the far right, the left is clear for WixUI's text.
- `dialog.bmp` (493×312, 24-bit): 164 px dark left panel with stalactites, mist, light motes and the mask; the rest is paper.
- `banner-dark.bmp` / `dialog-dark.bmp`: fully dark variants. Only useful with a custom UI whose text is bone-colored (`#E8E2D0`). WixUI draws black text, so the **light** variants are the ones in use.
- WiX variables: `WixUIBannerBmp` = banner.bmp, `WixUIDialogBmp` = dialog.bmp.
- Icon: `asyncronus.ico` (16–256 px), reference PNG `asyncronus-256.png`.

## Typography (ships with Windows)

1. **Constantia** (preferred: Roman serif with good contrast, close to the Trajan-like inscription feel, in a sober tone)
2. **Palatino Linotype**
3. **Georgia** (universal fallback)

Titles: serif. Body/UI: Segoe UI 9–10 pt (readability in lists, paths and errors). Never mix more than two families.

## Copy

Short sentences, second person, no exclamation marks, nothing copied from the game. The strings live in `wix/Asyncronus.es-ES.wxl` (Spanish).

## Regenerating

`make-assets.py` generates everything (Python + Pillow, fixed seeds); `verify-assets.py` checks sizes and formats (24-bit BMP, multi-size ICO).
