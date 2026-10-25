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

To publish a self-contained Windows x64 release:

```powershell
dotnet publish Clicko.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
```

The multi-resolution `Clicko.ico` is embedded as the executable's application icon and used for the WPF window icon. The published app does not need a separate .NET runtime installation.

The pet starts as an always-on-top, click-through overlay so it does not block other apps. Physical key presses and mouse-button clicks update the compact HUD and trigger their matching cat animation. Clicko counts events only; it does not retain typed text, key names, or mouse coordinates. The cat blinks at random intervals and falls asleep after 45 seconds without input.

- **Ctrl+Alt+B**: toggle between click-through and interactive mode
- **Ctrl+Alt+Q**: quit
- In interactive mode, drag the cat to move it; use the HUD buttons to switch modes or quit

If a global shortcut is already in use, Clicko reports it. If the toggle shortcut cannot be registered, the pet starts in interactive mode so it remains usable.

## Customize the pixel character

The separate 160-by-160 transparent PNGs in `Sprites` are cropped from [`Pixel Cat Companion Sprite Sheet.png`](Pixel%20Cat%20Companion%20Sprite%20Sheet.png): idle, blink, three keyboard-tap frames, three mouse-click frames, and a sleeping pose. The app loads these local files beside the executable, so it runs offline. Replace the PNGs with same-sized transparent sprites to customize the cat.
