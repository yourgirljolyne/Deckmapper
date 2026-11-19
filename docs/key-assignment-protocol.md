# VANGUARD AIR 99 key assignment notes

Source: Corsair Web Hub (`https://www.corsair.com/web-hub/`), read 2026-11-19, and tests on a real keyboard.
Transport (HID, Bragi v1.5 sessions, file read/write) is the same as for the screen; see SlotDeck's / TrackDeck's `vanguard-screen-protocol.md`.

## Where things live

File 15 is the profile list (`u16 header, u16 count, u16 profile ids`). The profile file (28000 on the test keyboard) is:

| offset | field |
|---|---|
| 0 | u16 header |
| 2 | u32 cookie |
| 6 | assignments file |
| 8 | properties file |
| 10 | archive file |
| 12 | lightings file |
| 14 | **key mappings file** |
| 16 | coolers settings |
| 18 | **dial modes file** |
| 20…29 | gamepad curves, firmware data, actuation files, lighting playback |
| 30 | u16 name length, then the name (+ NUL), then reset distances, rapid trigger, screen layout |

DeckMapper never writes the profile file. Web Hub bumps its cookie after key changes, but the keyboard picks up new assignments without it.

## Key ids

HID keyboard usages (A = 4 … F1 = 58 … F12 = 69, Delete = 76, arrows 79–82, numpad 83–99), except:
modifiers 105–111 (L Ctrl, L Shift, L Alt, L Win, R Ctrl, R Shift, R Alt; sent as HID 224–230), Space 44, FN 122,
Game Mode 130, side keys S1–S6 131–136, Copilot 137, Stream Deck key 138. Dial ring LED zones are 139–142 (lighting only).

## Key mappings file

`u16 header`, then one byte per key id. Bits say whether the key's **built-in** function is enabled on each layer:
bit 0 key alone, 1 FN, 2 FN2, 3 Ctrl, 4 Alt, 5 Shift. Default `0x3F`; `0` for ids that don't exist.
A cleared bit with an assignment uses the assignment; a cleared bit without one does nothing (disabled key).

## Assignments file (version 45)

`u16 45, u16 0`, then per key: `u8 key id, u8 count`, then `count ×` `u16 layer flags, u16 action file, u16 options`.
Layer flags: 0 alone, 1 FN, 2 FN2, 4 Ctrl, 8 Alt, 16 Shift. Options are trigger bits (1 on press, 64 stop on release; Web Hub writes `0x41`).

**The Alt + F4 problem:** Web Hub writes only layer 0 and clears only bit 0, so with Alt held the key uses its built-in Alt layer (the media function). Adding the same action for layers 4, 8 and 16 and clearing bits 3–5 fixes it. Verified on the keyboard, including Ctrl/Alt/Shift with remapped F-keys.

## Action files

Remap action: `u16 1902, u8 type, u16 duration (0), 3 zero bytes`, then:
- type 1 keyboard: `u8 count, count × u8 HID usage` (several = pressed together, e.g. Ctrl + C)
- type 2 mouse: `u8 button` (1 left, 2 right, 3 middle, 4 back, 5 forward)
- type 3 consumer: `u16 usage` (205 play/pause, 181 next, 182 previous, 183 stop, 226 mute, 233 vol+, 234 vol−)
- type 4 internal function, type 5 gamepad: `u16 code`

Other headers: 12460 macro, 940 text. DeckMapper keeps those untouched.
Web Hub takes file ids from 28000 upwards; DeckMapper takes new ones from 31999 downwards (SlotDeck uses 32699 downwards).

## Dial modes file

`u16 22, u16 count`, then per mode: `u8 type, 4 bytes RGBA ring colour, u8 n`, then `n ×` `u8 trigger, u8 0, u16 options, u16 action file`.
Types: 0 custom actions, 1 lighting brightness, 3 track jogging, 4 vertical scroll, 5 horizontal scroll, 6 switch apps, 7 zoom,
8 track selector, 9–12 Mac variants, 13 volume, 14 macro recording, 16 actuation distance, 17 screen brightness.
Triggers: 0 turn right, 1 turn left, 2 press. Options: bit 0 = trigger on each tick (Web Hub writes 1).

## Writing

Web Hub switches to host-controlled mode (property 3 = 2) around the writes and back to self-operated (1) after.
DeckMapper writes action files first, then the key mappings, assignments and dial files, and only the files whose bytes change.
