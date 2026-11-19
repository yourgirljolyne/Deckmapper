using System.Buffers.Binary;

namespace DeckMapper;

public enum StepKind { KeyDown, KeyUp, MouseDown, MouseUp, Wait, MoveTo, MoveBy, Scroll, ScrollSideways }

/// <summary>
/// One macro event. KeyDown/KeyUp: A = HID usage. MouseDown/Up: A = button (1 left, 2 right, 3 middle, 4 back, 5 forward).
/// Wait: A = milliseconds. Scroll / ScrollSideways: A = clicks (negative = down / left).
/// Only keys and waits play on the VANGUARD AIR 99 (tested 2026-11-19): mouse buttons and scrolling do nothing, and mouse
/// moves come out as stray key presses. DeckMapper only keeps those steps if a macro already has them.
/// </summary>
public sealed record MacroStep(StepKind Kind, int A, int B = 0)
{
    public string Describe() => Kind switch
    {
        StepKind.KeyDown => $"Press {KeyCodes.KeyName((ushort)A)}",
        StepKind.KeyUp => $"Release {KeyCodes.KeyName((ushort)A)}",
        StepKind.MouseDown => $"Press {KeyCodes.MouseName((ushort)A).Replace(" click", "").Replace(" button", "")} mouse button (not supported by this keyboard)",
        StepKind.MouseUp => $"Release {KeyCodes.MouseName((ushort)A).Replace(" click", "").Replace(" button", "")} mouse button (not supported by this keyboard)",
        StepKind.Wait => $"Wait {A} ms",
        StepKind.MoveTo => $"Move mouse to {A}, {B} (not supported by this keyboard)",
        StepKind.MoveBy => $"Move mouse by {A}, {B} (not supported by this keyboard)",
        StepKind.Scroll => $"Scroll {(A >= 0 ? "up" : "down")} {Math.Abs(A)} (not supported by this keyboard)",
        _ => $"Scroll {(A >= 0 ? "right" : "left")} {Math.Abs(A)} (not supported by this keyboard)",
    };
}

/// <summary>A macro: its steps and whether letting go of the key stops it.</summary>
public sealed record Macro(MacroStep[] Steps, bool StopOnRelease)
{
    // Assignment options. Web Hub's "play once" (press | stop on another action | stop on profile change = 49) makes this
    // keyboard LOOP the macro until another key is pressed (tested 2026-11-19), so it is not offered. While held =
    // press | stop on release, the same options plain key remaps use.
    public const ushort OptionsWhileHeld = 1 | 64, OptionsLoopsForever = 1 | 16 | 32;
    public const ushort OptionsOnce = OptionsWhileHeld; // until a real play-once setting is found
    public ushort Options => StopOnRelease ? OptionsWhileHeld : OptionsOnce;
    public const ushort Header = 12460;
    public const int MaxSteps = 1000;
    /// <summary>Longest single wait (written as 1-byte waits of up to 255 ms each).</summary>
    public const int MaxWaitMs = 60_000;

    public bool Same(Macro o) => StopOnRelease == o.StopOnRelease && Steps.SequenceEqual(o.Steps);

    public string Summary => $"Macro, {Steps.Length} step{(Steps.Length == 1 ? "" : "s")}";

    /// <summary>
    /// Macro file: u16 12460, u16 block count, 12 zero bytes, then events packed into 8-byte blocks.
    /// An event never straddles a block; the rest of a block is zero (no event).
    /// </summary>
    public byte[] Encode()
    {
        var o = new List<byte>(new byte[16]);
        BinaryPrimitives.WriteUInt16LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(o), Header);
        int used = 0;
        void Put(params byte[] ev)
        {
            if (used + ev.Length > 8) { o.AddRange(new byte[8 - used]); used = 0; }
            o.AddRange(ev);
            used += ev.Length;
            if (used == 8) used = 0;
        }
        static byte[] U16(int v) => [(byte)v, (byte)(v >> 8)];
        foreach (var s in Steps)
        {
            switch (s.Kind)
            {
                case StepKind.KeyDown: Put(1, (byte)s.A); break;
                case StepKind.KeyUp: Put(2, (byte)s.A); break;
                case StepKind.MouseDown: Put(3, (byte)s.A); break;
                case StepKind.MouseUp: Put(4, (byte)s.A); break;
                case StepKind.MoveBy: Put([5, .. U16((short)s.A), .. U16((short)s.B)]); break;
                case StepKind.MoveTo: Put([6, .. U16(s.A), .. U16(s.B)]); break;
                case StepKind.Scroll: Put(9, (byte)(sbyte)s.A); break;
                case StepKind.ScrollSideways: Put(10, (byte)(sbyte)s.A); break;
                case StepKind.Wait:
                    // Only the 1-byte wait (type 12) is proven on this keyboard; longer waits are chained.
                    for (int left = Math.Min(s.A, MaxWaitMs); left > 0; left -= 255) Put(12, (byte)Math.Min(left, 255));
                    break;
            }
        }
        if (used > 0) o.AddRange(new byte[8 - used]);
        var bytes = o.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)((bytes.Length - 16) / 8));
        return bytes;
    }

    /// <summary>Reads a macro file. Null when it holds events DeckMapper can't edit (random delays, packed moves).</summary>
    public static Macro? Decode(byte[] d, bool stopOnRelease)
    {
        if (d.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(d) != Header) return null;
        int blocks = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
        int end = Math.Min(d.Length, 16 + blocks * 8);
        var steps = new List<MacroStep>();
        int i = 16;
        while (i < end)
        {
            int blockEnd = 16 + ((i - 16) / 8 + 1) * 8;
            byte t = d[i];
            int Need(int n) => i + n <= Math.Min(end, blockEnd) ? n : -1;
            switch (t)
            {
                case 0: i = blockEnd; continue; // padding: rest of the block is empty
                case 1 or 2 or 3 or 4 or 9 or 10 or 12:
                    if (Need(2) < 0) return null;
                    byte v = d[i + 1];
                    steps.Add(t switch
                    {
                        1 => new(StepKind.KeyDown, v), 2 => new(StepKind.KeyUp, v), 3 => new(StepKind.MouseDown, v),
                        4 => new(StepKind.MouseUp, v), 9 => new(StepKind.Scroll, (sbyte)v), 10 => new(StepKind.ScrollSideways, (sbyte)v),
                        _ => new MacroStep(StepKind.Wait, v),
                    });
                    i += 2;
                    break;
                case 5 or 6:
                    if (Need(5) < 0) return null;
                    int x = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i + 1)), y = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i + 3));
                    steps.Add(t == 5 ? new(StepKind.MoveBy, (short)x, (short)y) : new(StepKind.MoveTo, x, y));
                    i += 5;
                    break;
                case 7:
                    if (Need(3) < 0) return null;
                    steps.Add(new(StepKind.Wait, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(i + 1))));
                    i += 3;
                    break;
                case 14:
                    if (Need(4) < 0) return null;
                    steps.Add(new(StepKind.Wait, d[i + 1] | d[i + 2] << 8 | d[i + 3] << 16));
                    i += 4;
                    break;
                default: return null; // 8, 11, 13: random delays and packed moves
            }
        }
        // Long waits are stored as chains of short ones; show them as one step.
        var merged = new List<MacroStep>();
        foreach (var st in steps)
            if (st.Kind == StepKind.Wait && merged.Count > 0 && merged[^1].Kind == StepKind.Wait) merged[^1] = merged[^1] with { A = merged[^1].A + st.A };
            else merged.Add(st);
        return new([.. merged], stopOnRelease);
    }
}
