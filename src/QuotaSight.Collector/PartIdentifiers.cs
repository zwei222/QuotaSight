namespace QuotaSight.Collector;

/// <summary>
/// Turns upstream client/provider/model values into the identifiers the API
/// stores, deterministically and without guessing.
///
/// The API only accepts identifiers in this normalized form (<c>isNormalizedClient</c>
/// and friends in <c>src/QuotaSight.Usage.Api/src/schema.ts</c>), so the two files
/// carry the same rules; the shared wire fixture keeps them from drifting.
///
/// The policy for a value that is not an identifier is always the same: replace it
/// with <see cref="TokscaleGraphContract.UnknownIdentifier"/> (<c>""</c>), never
/// with a repaired or truncated guess. A blank identifier says "observed usage
/// whose key is unknown", which is true; a repaired one would be an invention.
/// </summary>
public static class PartIdentifiers
{
    /// <summary>
    /// Suffixes of local weight files.
    ///
    /// A value ending in one of these names a file on disk, never a model id. The
    /// API applies the same list (`MODEL_FILE_SUFFIXES` in schema.ts).
    /// </summary>
    private static readonly string[] ModelFileSuffixes = [".gguf", ".safetensors", ".bin", ".onnx"];

    /// <summary>
    /// Normalizes a client id: lowercased (ASCII), then accepted only if it is a
    /// slug (<c>[a-z0-9][a-z0-9._-]{0,31}</c>).
    ///
    /// Casing is not meaningful in a client name — reporters write <c>Codex</c>,
    /// <c>codex</c> and <c>CODEX</c> for the same tool — so the value is folded
    /// first and the pattern is applied to the folded form, exactly as for
    /// provider and model. Only ASCII is folded: a non-ASCII value fails the
    /// pattern instead of being transformed into another string.
    /// </summary>
    /// <param name="raw">Upstream value, or <c>null</c> when the field was absent.</param>
    public static string NormalizeClient(string? raw)
    {
        if (raw is null || raw.Length is < 1 or > TokscaleGraphContract.MaxClientLength)
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        var lowered = AsciiLower(raw);
        if (!IsLowerAlphaNumeric(lowered[0]))
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        for (var index = 1; index < lowered.Length; index++)
        {
            var character = lowered[index];
            if (!IsLowerAlphaNumeric(character) && character is not ('.' or '_' or '-'))
            {
                return TokscaleGraphContract.UnknownIdentifier;
            }
        }

        return lowered;
    }

    /// <summary>
    /// Normalizes a provider id, which upstream may report as a comma-separated
    /// list of every provider that served a request.
    ///
    /// Split on <c>,</c>, trim, lowercase, drop empties, de-duplicate, sort, and
    /// re-join with <c>,</c>. More than
    /// <see cref="TokscaleGraphContract.MaxProviderElements"/> elements, or a joined
    /// value longer than <see cref="TokscaleGraphContract.MaxProviderLength"/>, is
    /// unknown rather than truncated: a truncated list would name the wrong set of
    /// providers.
    /// </summary>
    /// <param name="raw">Upstream value, or <c>null</c> when the field was absent.</param>
    public static string NormalizeProvider(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        var elements = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var piece in raw.Split(','))
        {
            var element = piece.Trim().ToLowerInvariant();
            if (element.Length == 0)
            {
                continue;
            }

            if (element.Length > TokscaleGraphContract.MaxProviderLength)
            {
                return TokscaleGraphContract.UnknownIdentifier;
            }

            elements.Add(element);
        }

        if (elements.Count is 0 or > TokscaleGraphContract.MaxProviderElements)
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        var joined = string.Join(',', elements);
        if (joined.Length > TokscaleGraphContract.MaxProviderLength)
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        foreach (var element in elements)
        {
            if (!IsProviderElement(element))
            {
                return TokscaleGraphContract.UnknownIdentifier;
            }
        }

        return joined;
    }

    /// <summary>
    /// Normalizes a model id: lowercased, then accepted only if it is an identifier
    /// and not a path.
    ///
    /// Local runtime adapters report the model they loaded, and for a local file
    /// that value is the file's path. A path is not a model id and must not leave
    /// the machine, so it becomes unknown. The distinguishing rules (see
    /// <see cref="IsPathShapedModel"/>) keep the legitimate <c>org/model</c> form
    /// while dropping anything that describes a location.
    /// </summary>
    /// <param name="raw">Upstream value, or <c>null</c> when the field was absent.</param>
    public static string NormalizeModel(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        var lowered = raw.ToLowerInvariant();
        if (lowered.Length > TokscaleGraphContract.MaxModelLength)
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        if (IsPathShapedModel(lowered) || !IsModelIdentifier(lowered))
        {
            return TokscaleGraphContract.UnknownIdentifier;
        }

        return lowered;
    }

    /// <summary>
    /// True when a (lowercased) model value describes a filesystem location rather
    /// than a model id.
    ///
    /// The rules are deliberately narrow so that a real identifier survives, and
    /// they are the same ones the API applies (<c>isPathShaped</c> in
    /// <c>src/QuotaSight.Usage.Api/src/schema.ts</c>):
    /// <list type="bullet">
    /// <item>a leading separator (<c>/…</c>, <c>\…</c>) — absolute path;</item>
    /// <item>any backslash — a Windows path separator, never an identifier;</item>
    /// <item>a drive letter (<c>c:…</c>) — a Windows path;</item>
    /// <item>a local weight-file suffix (<c>.gguf</c>, <c>.safetensors</c>,
    /// <c>.bin</c>, <c>.onnx</c>) — a file on disk, whatever its directory;</item>
    /// <item>two or more slashes — a directory traversal, as in
    /// <c>org/sub/model</c>. A single slash stays accepted, because
    /// <c>org/model</c> is a documented model id form and dropping it would fold
    /// real providers' ids into the unknown bucket.</item>
    /// </list>
    /// </summary>
    /// <param name="value">Model value, already lowercased.</param>
    public static bool IsPathShapedModel(string value)
    {
        if (value.StartsWith('/') || value.StartsWith('\\'))
        {
            return true;
        }

        if (value.Contains('\\', StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Length >= 2 && value[1] == ':' && IsLowerAlphaNumeric(value[0]))
        {
            return true;
        }

        foreach (var suffix in ModelFileSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '/')
            {
                slashes++;
            }
        }

        return slashes >= 2;
    }

    private static bool IsModelIdentifier(string value)
    {
        if (!IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsLowerAlphaNumeric(character)
                && character is not ('.' or '_' or ':' or '/' or '@' or '+' or '[' or ']' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsProviderElement(string value)
    {
        if (!IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsLowerAlphaNumeric(character)
                && character is not ('.' or '_' or ':' or '/' or '@' or '+' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAlphaNumeric(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    /// <summary>
    /// Folds ASCII letters to lower case, leaving every other character alone.
    ///
    /// Deliberately not <c>ToLowerInvariant</c>: that can change a string's length
    /// (`İ` becomes two characters) and would let a non-ASCII value turn into a
    /// different identifier instead of being rejected by the pattern.
    /// </summary>
    private static string AsciiLower(string value)
    {
        var buffer = value.ToCharArray();
        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] is >= 'A' and <= 'Z')
            {
                buffer[index] = (char)(buffer[index] + ('a' - 'A'));
            }
        }

        return new string(buffer);
    }
}
