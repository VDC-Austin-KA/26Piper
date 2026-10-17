using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace PipingSuite.Core;

/// <summary>Which part of a value steps: its last run of digits (01 → 02) or of letters (DETAIL A → DETAIL B).</summary>
public enum IncrementMode { Number, Letter }

public static class ValueSequence
{
    /// <summary>Last number or letter run, whichever ends the value; letters when it has no digits.</summary>
    public static IncrementMode Guess(string value) =>
        value.Length > 0 && char.IsDigit(value[^1]) || !value.Any(char.IsLetter) ? IncrementMode.Number : IncrementMode.Letter;

    /// <summary>
    /// <paramref name="value"/> stepped <paramref name="steps"/> times. Numbers keep their zero padding
    /// (09 → 10, 099 → 100); letters roll over like spreadsheet columns (Z → AA) and keep their case.
    /// A value with nothing to step is returned unchanged.
    /// </summary>
    public static string Step(string value, IncrementMode mode, int steps)
    {
        var m = Regex.Match(value, mode == IncrementMode.Number ? @"\d+(?!.*\d)" : @"[A-Za-z]+(?![\s\S]*[A-Za-z])");
        if (steps == 0 || !m.Success) return value;

        string next;
        if (mode == IncrementMode.Number)
        {
            if (!long.TryParse(m.Value, out var n)) return value;
            next = Math.Max(0, n + steps).ToString().PadLeft(m.Value.Length, '0');
        }
        else
        {
            long n = m.Value.ToUpperInvariant().Aggregate(0L, (acc, c) => acc * 26 + (c - 'A' + 1));
            next = Letters(Math.Max(1, n + steps));
            if (m.Value.All(char.IsLower)) next = next.ToLowerInvariant();
        }
        return value[..m.Index] + next + value[(m.Index + m.Length)..];
    }

    private static string Letters(long n)
    {
        var s = string.Empty;
        for (; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }
}
