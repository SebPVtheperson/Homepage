using System.IO;
using System.Text.RegularExpressions;

namespace Homepage;

public sealed record SharePointMetadata(string Title)
{
    public const string IconUrl = "https://static2.sharepointonline.com/files/fabric/assets/brand-icons/product/svg/sharepoint_48x1.svg";
    static readonly Lazy<Task<byte[]>> logo = new(() =>
    {
        using var stream = typeof(SharePointMetadata).Assembly.GetManifestResourceStream("Homepage.Assets.SharePoint.svg") ?? throw new InvalidDataException("SharePoint icon is missing.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return Icons.NormalizeAsync(bytes.ToArray());
    });
    public static Task<byte[]> LogoAsync() => logo.Value;
    public static bool IsSharePoint(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) && LayoutStore.IsWebsite(target) && uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase);
    public static SharePointMetadata? For(string target)
    {
        if (!IsSharePoint(target)) return null;
        var uri = new Uri(target);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        int siteIndex = Array.FindIndex(segments, s => s.Equals("sites", StringComparison.OrdinalIgnoreCase) || s.Equals("teams", StringComparison.OrdinalIgnoreCase));
        string title = "SharePoint";
        if (siteIndex >= 0 && siteIndex + 1 < segments.Length)
        {
            title = DisplayName(segments[siteIndex + 1]);
            if (siteIndex + 2 < segments.Length)
            {
                var library = segments[siteIndex + 2];
                if (library.Equals("Shared Documents", StringComparison.OrdinalIgnoreCase) || library.Equals("Documents", StringComparison.OrdinalIgnoreCase)) title += " - Documents";
                else if (!library.Equals("SitePages", StringComparison.OrdinalIgnoreCase) && !library.Equals("_layouts", StringComparison.OrdinalIgnoreCase)) title += " - " + DisplayName(library);
            }
        }
        return new(title);
    }
    static string DisplayName(string value)
    {
        value = Regex.Replace(value.Replace('_', ' ').Replace('-', ' '), "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Regex.Replace(value, "\\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }
    public static bool IsUsefulTitle(string? title, string target)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Equals(new Uri(target).Host, StringComparison.OrdinalIgnoreCase)) return false;
        return !Regex.IsMatch(title, "^(sign[ -]?in|log[ -]?in|access denied|authentication required)(\\b|$)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }
}
