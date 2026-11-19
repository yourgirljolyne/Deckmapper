namespace DeckMapper;

/// <summary>Names for the codes an action can send: HID keyboard usages, consumer (media) usages and mouse buttons.</summary>
internal static class KeyCodes
{
    public sealed record Named(ushort Code, string Name, string Group);

    /// <summary>HID keyboard usages (page 7) that a key can send, in picker order.</summary>
    public static readonly Named[] Keys = BuildKeys();

    private static Named[] BuildKeys()
    {
        var l = new List<Named>();
        for (int i = 0; i < 26; i++) l.Add(new((ushort)(4 + i), ((char)('A' + i)).ToString(), "Letters"));
        for (int i = 1; i <= 9; i++) l.Add(new((ushort)(29 + i), i.ToString(), "Numbers"));
        l.Add(new(39, "0", "Numbers"));
        for (int i = 1; i <= 12; i++) l.Add(new((ushort)(57 + i), "F" + i, "Function"));
        for (int i = 13; i <= 24; i++) l.Add(new((ushort)(91 + i), "F" + i, "Function"));
        l.AddRange([
            new(41, "Esc", "Editing"), new(43, "Tab", "Editing"), new(57, "Caps Lock", "Editing"), new(44, "Space", "Editing"),
            new(40, "Enter", "Editing"), new(42, "Backspace", "Editing"), new(73, "Insert", "Editing"), new(76, "Delete", "Editing"),
            new(74, "Home", "Navigation"), new(77, "End", "Navigation"), new(75, "Page Up", "Navigation"), new(78, "Page Down", "Navigation"),
            new(82, "Up", "Navigation"), new(81, "Down", "Navigation"), new(80, "Left", "Navigation"), new(79, "Right", "Navigation"),
            new(70, "Print Screen", "System"), new(71, "Scroll Lock", "System"), new(72, "Pause", "System"), new(101, "Menu", "System"),
            new(224, "Left Ctrl", "Modifiers"), new(225, "Left Shift", "Modifiers"), new(226, "Left Alt", "Modifiers"), new(227, "Left Win", "Modifiers"),
            new(228, "Right Ctrl", "Modifiers"), new(229, "Right Shift", "Modifiers"), new(230, "Right Alt", "Modifiers"), new(231, "Right Win", "Modifiers"),
            new(53, "` ~", "Symbols"), new(45, "- _", "Symbols"), new(46, "= +", "Symbols"), new(47, "[ {", "Symbols"), new(48, "] }", "Symbols"),
            new(49, "\\ |", "Symbols"), new(51, "; :", "Symbols"), new(52, "' \"", "Symbols"), new(54, ", <", "Symbols"), new(55, ". >", "Symbols"),
            new(56, "/ ?", "Symbols"), new(100, "\\ | (ISO)", "Symbols"),
            new(83, "Num Lock", "Numpad"), new(84, "Num /", "Numpad"), new(85, "Num *", "Numpad"), new(86, "Num -", "Numpad"),
            new(87, "Num +", "Numpad"), new(88, "Num Enter", "Numpad"), new(99, "Num .", "Numpad"), new(98, "Num 0", "Numpad"),
        ]);
        for (int i = 1; i <= 9; i++) l.Add(new((ushort)(88 + i), "Num " + i, "Numpad"));
        return [.. l];
    }

    /// <summary>Consumer-page usages. The first seven are the ones Web Hub offers; the rest are standard usages.</summary>
    public static readonly Named[] Media =
    [
        new(205, "Play / Pause", "Media"), new(183, "Stop", "Media"), new(182, "Previous track", "Media"), new(181, "Next track", "Media"),
        new(233, "Volume up", "Media"), new(234, "Volume down", "Media"), new(226, "Mute", "Media"),
        new(0x192, "Calculator", "Apps"), new(0x18A, "Mail", "Apps"), new(0x194, "File Explorer", "Apps"),
        new(0x223, "Browser home", "Browser"), new(0x224, "Browser back", "Browser"), new(0x225, "Browser forward", "Browser"),
        new(0x227, "Browser refresh", "Browser"), new(0x221, "Search", "Browser"),
    ];

    public static readonly Named[] Mouse =
    [
        new(1, "Left click", "Mouse"), new(2, "Right click", "Mouse"), new(3, "Middle click", "Mouse"),
        new(4, "Back button", "Mouse"), new(5, "Forward button", "Mouse"),
    ];

    public static string KeyName(ushort code) => Keys.FirstOrDefault(k => k.Code == code)?.Name ?? $"Key {code}";
    public static string MediaName(ushort code) => Media.FirstOrDefault(k => k.Code == code)?.Name ?? $"Media 0x{code:X}";
    public static string MouseName(ushort code) => Mouse.FirstOrDefault(k => k.Code == code)?.Name ?? $"Mouse {code}";

    public static bool IsModifier(ushort hid) => hid is >= 224 and <= 231;

    /// <summary>Corsair key ids for the modifier keys differ from HID (Web Hub's T7 table); everything else is the HID usage.</summary>
    public static ushort KeyIdToHid(byte keyId) => keyId switch
    {
        105 => 224, 106 => 225, 107 => 226, 108 => 227, 109 => 228, 110 => 229, 111 => 230,
        _ => keyId,
    };

    /// <summary>"Ctrl + Shift + Esc" style text for a list of HID usages.</summary>
    public static string ComboName(IEnumerable<ushort> codes) => string.Join(" + ", codes.Select(ShortName));

    private static string ShortName(ushort c) => c switch
    {
        224 or 228 => "Ctrl", 225 or 229 => "Shift", 226 or 230 => "Alt", 227 or 231 => "Win",
        _ => KeyName(c),
    };

    // ---------------- Windows key events -> HID ----------------

    /// <summary>Translates a WM_KEYDOWN / WM_SYSKEYDOWN into a HID usage. 0 when unknown.</summary>
    public static ushort FromWindows(int vk, long lParam)
    {
        bool ext = (lParam & (1L << 24)) != 0;
        int scan = (int)((lParam >> 16) & 0xFF);
        if (vk is >= 0x41 and <= 0x5A) return (ushort)(4 + vk - 0x41);           // A-Z
        if (vk is >= 0x31 and <= 0x39) return (ushort)(30 + vk - 0x31);          // 1-9
        if (vk == 0x30) return 39;
        if (vk is >= 0x70 and <= 0x7B) return (ushort)(58 + vk - 0x70);          // F1-F12
        if (vk is >= 0x7C and <= 0x87) return (ushort)(104 + vk - 0x7C);         // F13-F24
        if (vk is >= 0x61 and <= 0x69) return (ushort)(89 + vk - 0x61);          // Num 1-9
        return vk switch
        {
            0x60 => 98, 0x6E => 99, 0x6F => 84, 0x6A => 85, 0x6D => 86, 0x6B => 87, 0x90 => 83,
            0x0D => (ushort)(ext ? 88 : 40),
            0x1B => 41, 0x08 => 42, 0x09 => 43, 0x20 => 44, 0x14 => 57,
            0xBD => 45, 0xBB => 46, 0xDB => 47, 0xDD => 48, 0xDC => 49, 0xBA => 51, 0xDE => 52, 0xC0 => 53,
            0xBC => 54, 0xBE => 55, 0xBF => 56, 0xE2 => 100,
            0x2C => 70, 0x91 => 71, 0x13 => 72, 0x2D => 73, 0x24 => 74, 0x21 => 75, 0x2E => 76, 0x23 => 77, 0x22 => 78,
            0x27 => 79, 0x25 => 80, 0x28 => 81, 0x26 => 82, 0x5D => 101,
            0x10 => (ushort)(scan == 0x36 ? 229 : 225),
            0x11 => (ushort)(ext ? 228 : 224),
            0x12 => (ushort)(ext ? 230 : 226),
            0xA0 => 225, 0xA1 => 229, 0xA2 => 224, 0xA3 => 228, 0xA4 => 226, 0xA5 => 230,
            0x5B => 227, 0x5C => 231,
            _ => 0,
        };
    }
}
