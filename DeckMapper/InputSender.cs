using System.Runtime.InteropServices;

namespace DeckMapper;

/// <summary>Sends key, media and mouse input to Windows (SendInput), translated from HID usages.</summary>
internal static class InputSender
{
    public static void KeyDown(ushort hid) => Key(hid, up: false);
    public static void KeyUp(ushort hid) => Key(hid, up: true);

    private static void Key(ushort hid, bool up)
    {
        var (vk, ext) = HidToVk(hid);
        if (vk == 0) return;
        SendKey(vk, ext, up);
    }

    /// <summary>Consumer-page usage (media keys) as a Windows virtual key press or release.</summary>
    public static void Media(ushort usage, bool up)
    {
        ushort vk = usage switch
        {
            205 => 0xB3, 183 => 0xB2, 182 => 0xB1, 181 => 0xB0, 233 => 0xAF, 234 => 0xAE, 226 => 0xAD,
            0x192 => 0xB7, 0x18A => 0xB4, 0x194 => 0xB6,
            0x223 => 0xAC, 0x224 => 0xA6, 0x225 => 0xA7, 0x227 => 0xA8, 0x221 => 0xAA,
            _ => 0,
        };
        if (vk != 0) SendKey(vk, true, up);
    }

    /// <summary>Mouse button 1 left, 2 right, 3 middle, 4 back, 5 forward.</summary>
    public static void MouseButton(int button, bool up)
    {
        uint flags = button switch
        {
            1 => up ? 0x0004u : 0x0002u,
            2 => up ? 0x0010u : 0x0008u,
            3 => up ? 0x0040u : 0x0020u,
            4 or 5 => up ? 0x0100u : 0x0080u,
            _ => 0,
        };
        if (flags == 0) return;
        Mouse(0, 0, button switch { 4 => 1u, 5 => 2u, _ => 0u }, flags);
    }

    /// <summary>Absolute move in the keyboard's macro units (0-32767 across the primary screen).</summary>
    public static void MoveTo(int x, int y) => Mouse(Math.Clamp(x, 0, 32767) * 2, Math.Clamp(y, 0, 32767) * 2, 0, 0x8001);
    public static void MoveBy(int dx, int dy) => Mouse(dx, dy, 0, 0x0001);
    public static void Scroll(int clicks, bool sideways) => Mouse(0, 0, unchecked((uint)(clicks * 120)), sideways ? 0x01000u : 0x0800u);

    // ---------------- HID usage -> virtual key ----------------

    /// <summary>The reverse of <see cref="KeyCodes.FromWindows"/>: virtual key and whether it's an extended key.</summary>
    public static (ushort vk, bool ext) HidToVk(ushort hid)
    {
        if (hid is >= 4 and <= 29) return ((ushort)(0x41 + hid - 4), false);    // A-Z
        if (hid is >= 30 and <= 38) return ((ushort)(0x31 + hid - 30), false);   // 1-9
        if (hid is >= 58 and <= 69) return ((ushort)(0x70 + hid - 58), false);   // F1-F12
        if (hid is >= 104 and <= 115) return ((ushort)(0x7C + hid - 104), false); // F13-F24
        if (hid is >= 89 and <= 97) return ((ushort)(0x61 + hid - 89), false);   // Num 1-9
        return hid switch
        {
            39 => (0x30, false), 40 => (0x0D, false), 41 => (0x1B, false), 42 => (0x08, false), 43 => (0x09, false), 44 => (0x20, false),
            45 => (0xBD, false), 46 => (0xBB, false), 47 => (0xDB, false), 48 => (0xDD, false), 49 => (0xDC, false),
            51 => (0xBA, false), 52 => (0xDE, false), 53 => (0xC0, false), 54 => (0xBC, false), 55 => (0xBE, false), 56 => (0xBF, false),
            57 => (0x14, false), 70 => (0x2C, true), 71 => (0x91, false), 72 => (0x13, false),
            73 => (0x2D, true), 74 => (0x24, true), 75 => (0x21, true), 76 => (0x2E, true), 77 => (0x23, true), 78 => (0x22, true),
            79 => (0x27, true), 80 => (0x25, true), 81 => (0x28, true), 82 => (0x26, true),
            83 => (0x90, true), 84 => (0x6F, true), 85 => (0x6A, false), 86 => (0x6D, false), 87 => (0x6B, false), 88 => (0x0D, true),
            98 => (0x60, false), 99 => (0x6E, false), 100 => (0xE2, false), 101 => (0x5D, true),
            224 => (0xA2, false), 225 => (0xA0, false), 226 => (0xA4, false), 227 => (0x5B, true),
            228 => (0xA3, true), 229 => (0xA1, false), 230 => (0xA5, true), 231 => (0x5C, true),
            _ => (0, false),
        };
    }

    // ---------------- SendInput ----------------

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

    private static void SendKey(ushort vk, bool ext, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = vk;
        i.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        i.u.ki.dwFlags = (ext ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        SendInput(1, [i], Marshal.SizeOf<INPUT>());
    }

    private static void Mouse(int dx, int dy, uint data, uint flags)
    {
        var i = new INPUT { type = INPUT_MOUSE };
        i.u.mi.dx = dx;
        i.u.mi.dy = dy;
        i.u.mi.mouseData = data;
        i.u.mi.dwFlags = flags;
        SendInput(1, [i], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}
