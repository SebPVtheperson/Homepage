using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Homepage;

public sealed class WebsiteCache
{
    public sealed class Index
    {
        public Dictionary<string, PageMetadata> Pages { get; set; } = [];
        public Dictionary<string, string> Sites { get; set; } = [];
        public Dictionary<string, string> IconUrls { get; set; } = [];
    }
    readonly object gate = new();
    readonly LayoutStore? store;
    readonly IconAssets? assets;
    readonly Dictionary<string, byte[]> memory = [];
    Index index = new();
    string? CachePath => store is null ? null : Path.Combine(store.DataDirectory, "website-cache.json");
    public WebsiteCache(LayoutStore? store)
    {
        this.store = store; assets = store is null ? null : new IconAssets(store);
        if (CachePath is { } path && File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Index>(File.ReadAllText(path));
                if (saved?.Pages is not null && saved.Sites is not null && saved.IconUrls is not null) index = saved;
            }
            catch { /* Metadata is disposable; an unreadable cache can be rebuilt without touching the user's layout. */ }
        }
    }
    public static string PageKey(string url) => new UriBuilder(url) { Fragment = "" }.Uri.AbsoluteUri;
    public static string SiteKey(string url)
    {
        var uri = new Uri(url); var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
    }
    static string UrlKey(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    public PageMetadata? GetPage(string url)
    {
        lock (gate) return index.Pages.GetValueOrDefault(PageKey(url));
    }
    public void RememberPage(string url, PageMetadata page)
    {
        lock (gate)
        {
            var key = PageKey(url);
            if (index.Pages.TryGetValue(key, out var old) && old is not null && old.Title == page.Title && old.Icons is not null && old.Icons.SequenceEqual(page.Icons)) return;
            index.Pages[key] = page; Save();
        }
    }
    public byte[]? SiteIcon(string site)
    {
        lock (gate) return Read(index.Sites.GetValueOrDefault(site));
    }
    public byte[]? UrlIcon(string url)
    {
        lock (gate) return Read(index.IconUrls.GetValueOrDefault(UrlKey(url)));
    }
    public void RememberIcon(string site, string url, byte[] png)
    {
        lock (gate)
        {
            var relative = assets?.StorePng(png) ?? "icons/" + Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() + ".png";
            memory[relative] = png;
            var key = UrlKey(url);
            if (index.Sites.GetValueOrDefault(site) == relative && index.IconUrls.GetValueOrDefault(key) == relative) return;
            index.Sites[site] = relative; index.IconUrls[key] = relative; Save();
        }
    }
    byte[]? Read(string? relative)
    {
        if (relative is null) return null;
        if (memory.TryGetValue(relative, out var bytes)) return bytes;
        if (store is null) return null;
        try
        {
            var path = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), store.DataDirectory);
            if (!path.StartsWith(Path.Combine(store.DataDirectory, "icons") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            bytes = File.ReadAllBytes(path);
            if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71) return null;
            return memory[relative] = bytes;
        }
        catch { return null; }
    }
    void Save()
    {
        if (CachePath is not { } path) return;
        Directory.CreateDirectory(store!.DataDirectory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(index, LayoutStore.JsonOptions));
        File.Move(temporary, path, true);
    }
}
