namespace QuotaSight.Infrastructure;

public static class GitHubOrganizationSlug
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 39 || value[0] == '-' || value[^1] == '-')
            return false;

        var previousWasHyphen = false;
        foreach (var character in value)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                    return false;
                previousWasHyphen = true;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(character))
                return false;
            previousWasHyphen = false;
        }

        return true;
    }
}
