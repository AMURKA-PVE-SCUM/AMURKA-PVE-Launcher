using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AmurkaLauncher.Core.Domain.ValueObjects;

public readonly record struct SteamId64 :
    ISpanParsable<SteamId64>,
    ISpanFormattable,
    IUtf8SpanFormattable
{
    private static readonly SearchValues<char> Digits = SearchValues.Create("0123456789");

    public const ulong Steam64Base = 76561197960265728UL;

    public string Value { get; }

    public bool IsValid => Value.Length == 17 && Value.StartsWith("7656119", StringComparison.Ordinal) && !Value.AsSpan().ContainsAnyExcept(Digits) && ulong.TryParse(Value, out _);

    public SteamId64(string value)
    {
        Value = value?.Trim() ?? string.Empty;
    }

    public SteamId64(ulong numericValue)
    {
        Value = numericValue.ToString(CultureInfo.InvariantCulture);
    }

    public static SteamId64 FromAccountId(uint accountId)
    {
        return new SteamId64(Steam64Base + accountId);
    }

    public static SteamId64 Parse(string s, IFormatProvider? provider = null) =>
        Parse(s.AsSpan(), provider);

    public static SteamId64 Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException("Неверный формат SteamID64");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out SteamId64 result) =>
        TryParse(s.AsSpan(), provider, out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out SteamId64 result)
    {
        ReadOnlySpan<char> trimmed = s.Trim();
        if (trimmed.Length == 17 && trimmed.StartsWith("7656119") && !trimmed.ContainsAnyExcept(Digits) && ulong.TryParse(trimmed, provider, out ulong val))
        {
            result = new SteamId64(val);
            return true;
        }
        result = default;
        return false;
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (Value.AsSpan().TryCopyTo(destination))
        {
            charsWritten = Value.Length;
            return true;
        }
        charsWritten = 0;
        return false;
    }

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        if (ulong.TryParse(Value, out ulong val))
        {
            return Utf8Formatter.TryFormat(val, utf8Destination, out bytesWritten);
        }
        bytesWritten = 0;
        return false;
    }

    public static implicit operator string(SteamId64 id) => id.Value;

    public override string ToString() => Value;

    public string ToString(string? format, IFormatProvider? formatProvider) => Value;
}
