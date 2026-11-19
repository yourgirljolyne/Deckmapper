namespace DeckMapper;

/// <summary>Dial mode types and triggers (Web Hub's control dial enums).</summary>
internal static class DialNames
{
    public const byte Custom = 0;
    public const byte TurnRight = 0, TurnLeft = 1, Press = 2;

    /// <summary>Modes offered in the picker, in a sensible order. Mac-only modes are left out.</summary>
    public static readonly (byte Type, string Name)[] Modes =
    [
        (13, "Volume"),
        (4, "Scroll up / down"),
        (5, "Scroll left / right"),
        (7, "Zoom"),
        (6, "Switch apps"),
        (3, "Seek in track"),
        (8, "Change track"),
        (1, "Lighting brightness"),
        (17, "Screen brightness"),
        (Custom, "Custom actions"),
    ];

    public static string Name(byte type) => Modes.FirstOrDefault(m => m.Type == type).Name ?? type switch
    {
        9 => "Seek (Mac Music)", 10 => "Seek (Mac Spotify)", 11 => "Switch apps (Mac)", 12 => "Zoom (Mac)",
        14 => "Macro recording", 16 => "Actuation distance",
        _ => $"Mode {type}",
    };

    public static string Trigger(byte t) => t switch { TurnRight => "turn right", TurnLeft => "turn left", Press => "press", _ => $"trigger {t}" };
}
