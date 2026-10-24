# Clicko

A small, transparent WPF desktop pet for Windows. Clicko counts physical keyboard presses and mouse-button clicks while animating a pixel-art cat. It counts activity only: it does not store key names, typed text, mouse positions, or click details. Injected input is ignored.

## Requirements

- Windows
- .NET 8 SDK

## Run

From this folder:

```powershell
dotnet run
```

The pet starts as an always-on-top, click-through overlay so it does not block other apps. Physical key presses and mouse-button clicks update the compact HUD and trigger their matching cat animation. Clicko counts events only; it does not retain typed text, key names, or mouse coordinates.

- **Ctrl+Alt+B**: toggle between click-through and interactive mode
- **Ctrl+Alt+Q**: quit
- In interactive mode, drag the cat to move it; use the HUD buttons to switch modes or quit

If a global shortcut is already in use, Clicko reports it. If the toggle shortcut cannot be registered, the pet starts in interactive mode so it remains usable.

## Customize the pixel character

The separate 160-by-160 transparent PNGs in `Sprites` are cropped from [`Pixel Cat Companion Sprite Sheet.png`](Pixel%20Cat%20Companion%20Sprite%20Sheet.png): idle and blink, three keyboard-tap frames, and three mouse-click frames. The app loads these local files beside the executable, so it runs offline. Replace the PNGs with same-sized transparent sprites to customize the cat.
