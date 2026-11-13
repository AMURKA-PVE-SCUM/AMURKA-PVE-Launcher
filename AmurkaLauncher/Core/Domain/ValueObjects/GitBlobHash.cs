using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;

namespace AmurkaLauncher.Core.Domain.ValueObjects;

public readonly record struct GitBlobHash :
    ISpanParsable<GitBlobHash>,
    ISpanFormattable,
    IUtf8SpanFormattable
{
    private static readonly SearchValues<char> HexChars = SearchValues.Create("0123456789abcdefABCDEF");

    public string Value { get; }

    public bool IsValid => Value.Length == 40 && !Value.AsSpan().ContainsAnyExcept(HexChars);

    public GitBlobHash(string value)
    {
        Value = value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    public static GitBlobHash FromBytes(ReadOnlySpan<byte> hashBytes)
    {
        return new GitBlobHash(Convert.ToHexString(hashBytes).ToLowerInvariant());
    }

    public static GitBlobHash Parse(string s, IFormatProvider? provider = null) =>
        Parse(s.AsSpan(), provider);

    public static GitBlobHash Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException("Неверный формат GitBlobHash");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out GitBlobHash result) =>
        TryParse(s.AsSpan(), provider, out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out GitBlobHash result)
    {
        ReadOnlySpan<char> trimmed = s.Trim();
        if (trimmed.Length == 40 && !trimmed.ContainsAnyExcept(HexChars))
        {
            result = new GitBlobHash(trimmed.ToString());
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
        if (System.Text.Encoding.UTF8.TryGetBytes(Value, utf8Destination, out bytesWritten))
        {
            return true;
        }
        bytesWritten = 0;
        return false;
    }

    public static implicit operator string(GitBlobHash hash) => hash.Value;

    public override string ToString() => Value;

    public string ToString(string? format, IFormatProvider? formatProvider) => Value;
}
