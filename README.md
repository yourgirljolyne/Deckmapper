# DeckMapper

**Remap the keys and the dial on your Corsair VANGUARD AIR 99. No iCUE required.**

DeckMapper is a small window with a picture of the keyboard. Click a key, pick what it should do, press **Apply to keyboard**. The settings are saved on the keyboard itself, so they keep working without DeckMapper running.

> Not affiliated with or endorsed by Corsair. The key assignment format was worked out from Corsair's own public Web Hub app. Use at your own risk.

## Why this exists: Alt + F4

Out of the box the F-row does media things and FN + F4 is F4. If you remap F4 to F4 in Web Hub, plain F4 works, but **Alt + F4 doesn't**: you're back to Alt + FN + F4, two hands.

The keyboard keeps a separate layer for each modifier: one for the key alone, one with FN, and one each with Ctrl, Alt and Shift held. Web Hub only fills in the first, so with Alt held the key falls back to its media default. DeckMapper writes the same action into the Ctrl, Alt and Shift layers too, so remapped keys behave like real keys. Keys that still have this gap get an orange corner, and **Fix them all** fixes every one at once. If you want Ctrl/Alt/Shift + a key to keep its built-in function on purpose, tick **Don't warn about Ctrl / Alt / Shift for this key** and it's left alone (that choice is saved on your PC).

## What it does

- **Main layer and FN layer.** Any key can send another key, a shortcut (record it, e.g. Ctrl + Shift + Esc), a media/app key, a mouse button, or nothing.
- **Macros.** Record what you type (with the time between keys) or build it step by step: key presses, key down / key up, waits. A macro plays once per press, or, if you pick that, runs while you hold its key and stops when you let go. The keyboard only plays keys and waits: mouse clicks, scrolling and mouse moves don't work in its macros, so DeckMapper doesn't offer them.
- **Volume dial.** Add, remove, reorder and recolour its modes (volume, scrolling, zoom, switching apps, track control, brightness), or give it custom actions for turn left, turn right and press.
- **F-keys first preset.** F1–F12 send F-keys, and FN + F7–F12 control media.
- **Key tester.** Click the strip at the bottom and press keys to see exactly what Windows receives, with a short history so repeats stand out. Alt + F4 won't close anything there.
- **Backups.** Before every write, the current setup is saved under `%LOCALAPPDATA%\DeckMapper\backups`. **Backups…** puts one back.

It leaves lighting, macros made in Web Hub, profiles and the screen alone. The one thing it writes in the profile is the same "changed" timestamp Web Hub writes, so the keyboard reloads its keys straight away. Assignments it doesn't understand are kept as they are.

## Get it running

1. Grab `DeckMapper.exe` from [Releases](../../releases), or build it yourself (below).
2. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it.
3. Plug the keyboard in with the **USB cable** and close iCUE.
4. Run `DeckMapper.exe`.

The profile needs key assignment files, which Web Hub creates the first time you remap any key. If DeckMapper says they're missing, remap one key in Web Hub once.

## Command line

```
DeckMapper dump              # print every remapped key and dial mode (read-only)
DeckMapper check             # confirm DeckMapper would write nothing for an unchanged setup (read-only)
DeckMapper restore <folder>  # put a backup back
```

## Build it yourself

Needs the .NET 10 SDK on Windows.

```
cd DeckMapper
dotnet publish -c Release -r win-x64 -o dist -p:PublishSingleFile=true -p:SelfContained=false
```

How it talks to the keyboard: [docs/key-assignment-protocol.md](docs/key-assignment-protocol.md).
