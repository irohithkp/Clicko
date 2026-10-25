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

## Build the Windows installer

Install Inno Setup 6, then run this command from the project folder (or use the Inno Setup Command Prompt):

```powershell
ISCC.exe installer.iss
```

The installer script packages the complete `publish\win-x64` folder, so publish the application first. It installs Clicko under Program Files by default, lets the user choose another destination, adds a Start Menu shortcut, offers an optional desktop shortcut, registers a normal Windows uninstaller, and shows a launch option when setup finishes. The custom `Clicko.ico` is used by setup and the installed app.

The installer is created at `publish\installer\Clicko-Setup-v0.2.0.exe`.

The pet opens centered on screen and remains an always-on-top, transparent overlay. Click and hold the visible cat or keyboard artwork to drag Clicko; it stays where you release it. Transparent areas around the artwork pass clicks through to other apps. Physical key presses and mouse-button clicks update the compact HUD and trigger their matching cat animation. Clicko counts events and uses live cursor coordinates only for hit testing and temporary drag calculations; it does not record or persist typed text, key names, mouse positions, or click details. The cat blinks at random intervals and falls asleep after 45 seconds without input.

- **Ctrl+Alt+Q**: quit

If the quit shortcut is already in use, Clicko reports it.

## Customize the pixel character

The separate 160-by-160 transparent PNGs in `Sprites` are cropped from [`Pixel Cat Companion Sprite Sheet.png`](Pixel%20Cat%20Companion%20Sprite%20Sheet.png): idle, blink, three keyboard-tap frames, three mouse-click frames, and a sleeping pose. The app loads these local files beside the executable, so it runs offline. Replace the PNGs with same-sized transparent sprites to customize the cat.
