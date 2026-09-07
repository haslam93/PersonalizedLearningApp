namespace UpskillTracker.Services;

public static class LocalReturnUrl
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsLocal(value))
        {
            return "/";
        }

        return IsLocal(Uri.UnescapeDataString(value)) ? value : "/";
    }

    private static bool IsLocal(string value) =>
        value.StartsWith('/') &&
        (value.Length == 1 || value[1] is not ('/' or '\\')) &&
        !value.Any(character => character == '\\' || char.IsControl(character));
}
