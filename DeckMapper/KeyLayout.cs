namespace DeckMapper;

/// <summary>A physical key: Corsair key id, cap label, position in key units, and what it does by default on each layer.</summary>
internal sealed record PhysicalKey(byte Id, string Label, double X, double Y, double W = 1, double H = 1,
    string? MainDefault = null, string? FnDefault = null)
{
    /// <summary>The key's own function on the main layer.</summary>
    public string MainText => MainDefault ?? Label;
    /// <summary>What FN + key does out of the box (unknown FN combos are shown as the key itself).</summary>
    public string FnText => FnDefault ?? MainText;
}

/// <summary>
/// VANGUARD AIR 99 layout (North America), taken from Corsair Web Hub's kb2B21 keyboard view.
/// Key ids are HID usages except modifiers (105-111), FN (122), the side keys S1-S6 (131-136),
/// Copilot (137), Game Mode (130) and the Stream Deck key (138).
/// </summary>
internal static class KeyLayout
{
    public const byte FnKey = 122;
    public const double Width = 22.75, Height = 6.5;

    public static readonly PhysicalKey[] Keys = Build();

    private static PhysicalKey[] Build()
    {
        var k = new List<PhysicalKey>();
        // Side column S1-S6.
        for (int i = 0; i < 6; i++) k.Add(new((byte)(131 + i), "S" + (i + 1), 0, i == 0 ? 0 : 1.4 + (i - 1)));

        const double x0 = 1.5, top = 0, row = 1.4; // main block starts here; rows below the F row are offset
        // F row: Esc, F1-F12 (media by default, F-keys with FN), Game Mode, Stream Deck key, Delete.
        string[] fMain = ["Brightness −", "Brightness +", "Task View", "Search", "Mic", "Sleep",
            "Prev track", "Play / Pause", "Next track", "Mute", "Volume −", "Volume +"];
        k.Add(new(41, "Esc", x0, top));
        for (int i = 0; i < 12; i++)
        {
            double x = x0 + 1.5 + i + (i / 4) * 0.25;
            k.Add(new((byte)(58 + i), "F" + (i + 1), x, top, MainDefault: fMain[i], FnDefault: "F" + (i + 1)));
        }
        k.Add(new(130, "Game", x0 + 14.25, top, MainDefault: "Game Mode"));
        k.Add(new(138, "SD", x0 + 15.25, top, MainDefault: "Stream Deck key"));
        k.Add(new(76, "Del", x0 + 16.25, top, MainDefault: "Delete"));

        double y = row;
        Row(k, x0, y, ("` ~", 53, 1), ("1", 30, 1), ("2", 31, 1), ("3", 32, 1), ("4", 33, 1), ("5", 34, 1), ("6", 35, 1), ("7", 36, 1),
            ("8", 37, 1), ("9", 38, 1), ("0", 39, 1), ("- _", 45, 1), ("= +", 46, 1), ("Backspace", 42, 2));
        Row(k, x0, y + 1, ("Tab", 43, 1.5), ("Q", 20, 1), ("W", 26, 1), ("E", 8, 1), ("R", 21, 1), ("T", 23, 1), ("Y", 28, 1), ("U", 24, 1),
            ("I", 12, 1), ("O", 18, 1), ("P", 19, 1), ("[ {", 47, 1), ("] }", 48, 1), ("\\ |", 49, 1.5));
        Row(k, x0, y + 2, ("Caps", 57, 1.75), ("A", 4, 1), ("S", 22, 1), ("D", 7, 1), ("F", 9, 1), ("G", 10, 1), ("H", 11, 1), ("J", 13, 1),
            ("K", 14, 1), ("L", 15, 1), ("; :", 51, 1), ("' \"", 52, 1), ("Enter", 40, 2.25));
        Row(k, x0, y + 3, ("Shift", 106, 2.25), ("Z", 29, 1), ("X", 27, 1), ("C", 6, 1), ("V", 25, 1), ("B", 5, 1), ("N", 17, 1), ("M", 16, 1),
            (", <", 54, 1), (". >", 55, 1), ("/ ?", 56, 1), ("Shift", 110, 1.75), ("↑", 82, 1));
        Row(k, x0, y + 4, ("Ctrl", 105, 1.25), ("Win", 108, 1.25), ("Alt", 107, 1.25), ("Space", 44, 6.25), ("Alt", 111, 1), ("FN", FnKey, 1),
            ("Copilot", 137, 1), ("←", 80, 1), ("↓", 81, 1), ("→", 79, 1));

        // Column between the main block and the numpad.
        k.Add(new(74, "Home", x0 + 15, y));
        k.Add(new(75, "PgUp", x0 + 15, y + 1));
        k.Add(new(78, "PgDn", x0 + 15, y + 2));
        k.Add(new(77, "End", x0 + 15, y + 3));

        // Numpad.
        double n = x0 + 16.75;
        Row(k, n, y, ("Num", 83, 1), ("/", 84, 1), ("*", 85, 1), ("−", 86, 1));
        Row(k, n, y + 1, ("7", 95, 1), ("8", 96, 1), ("9", 97, 1));
        Row(k, n, y + 2, ("4", 92, 1), ("5", 93, 1), ("6", 94, 1));
        Row(k, n, y + 3, ("1", 89, 1), ("2", 90, 1), ("3", 91, 1));
        Row(k, n, y + 4, ("0", 98, 2), (".", 99, 1));
        k.Add(new(87, "+", n + 3, y + 1, H: 2));
        k.Add(new(88, "Ent", n + 3, y + 3, H: 2));

        // Friendlier names for the side panel.
        return k.Select(p => p.Id switch
        {
            105 => p with { MainDefault = "Left Ctrl" }, 106 => p with { MainDefault = "Left Shift" },
            107 => p with { MainDefault = "Left Alt" }, 108 => p with { MainDefault = "Windows" },
            110 => p with { MainDefault = "Right Shift" }, 111 => p with { MainDefault = "Right Alt" },
            137 => p with { MainDefault = "Copilot" },
            >= 131 and <= 136 => p with { MainDefault = $"Side key {p.Label}" },
            _ => p,
        }).ToArray();
    }

    private static void Row(List<PhysicalKey> k, double x, double y, params (string label, int id, double w)[] keys)
    {
        foreach (var (label, id, w) in keys)
        {
            k.Add(new((byte)id, label, x, y, w));
            x += w;
        }
    }

    public static PhysicalKey? Find(byte id) => Keys.FirstOrDefault(k => k.Id == id);

    /// <summary>The dial sits above the numpad, next to the screen.</summary>
    public static readonly (double X, double Y, double D) Dial = (19.25 + 1.5 - 0.25, -0.05, 1.1);
}
