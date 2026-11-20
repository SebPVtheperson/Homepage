using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Homepage;

public enum BookmarkBrowser { Edge, Chrome, Firefox, Brave, Opera }
public sealed record BookmarkProfile(string Name, string Path);
public sealed class BookmarkNode
{
    public string Name { get; set; } = "Untitled";
    public string? Url { get; set; }
    public bool Folder { get; set; }
    public List<BookmarkNode> Children { get; set; } = [];
    public bool Supported => !Folder && Url is not null && LayoutStore.IsWebsite(Url);
    public IEnumerable<BookmarkNode> Descendants() => new[] { this }.Concat(Children.SelectMany(n => n.Descendants()));
    public int Skipped => Descendants().Count(n => !n.Folder && !n.Supported);
}

public static class BookmarkReader
{
    public static List<BookmarkProfile> Discover(BookmarkBrowser browser, string? local = null, string? roaming = null, CancellationToken token = default)
    {
        local ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roaming ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var result = new List<BookmarkProfile>();
        if (browser == BookmarkBrowser.Opera)
        {
            foreach (var product in new[] { "Opera Stable", "Opera GX Stable" })
            {
                var root = System.IO.Path.Combine(roaming, "Opera Software", product);
                if (!Directory.Exists(root)) continue;
                var folders = new[] { root }.Concat(Directory.EnumerateDirectories(root).Where(d => System.IO.Path.GetFileName(d) is var id && (id == "Default" || id.StartsWith("Profile ", StringComparison.Ordinal))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                foreach (var folder in folders)
                {
                    token.ThrowIfCancellationRequested(); var path = System.IO.Path.Combine(folder, "Bookmarks");
                    if (File.Exists(path)) result.Add(new(product + (folder == root ? "" : " · " + System.IO.Path.GetFileName(folder)), path));
                }
            }
        }
        else if (browser != BookmarkBrowser.Firefox)
        {
            var root = System.IO.Path.Combine(local, browser == BookmarkBrowser.Chrome ? "Google/Chrome/User Data" : browser == BookmarkBrowser.Brave ? "BraveSoftware/Brave-Browser/User Data" : "Microsoft/Edge/User Data");
            if (!Directory.Exists(root)) return result;
            var names = new Dictionary<string, string>();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root, "Local State")));
                if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var cache))
                    foreach (var entry in cache.EnumerateObject()) names[entry.Name] = entry.Value.TryGetProperty("name", out var name) ? name.GetString() ?? entry.Name : entry.Name;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested(); var id = System.IO.Path.GetFileName(directory);
                if (id is "Guest Profile" or "System Profile" || !(id == "Default" || id.StartsWith("Profile ", StringComparison.Ordinal) || names.ContainsKey(id))) continue;
                var path = System.IO.Path.Combine(directory, "Bookmarks");
                if (File.Exists(path)) result.Add(new(names.GetValueOrDefault(id, id) + " · " + id, path));
            }
        }
        else
        {
            var root = System.IO.Path.Combine(roaming, "Mozilla/Firefox");
            var configs = new List<string> { System.IO.Path.Combine(root, "profiles.ini") };
            var groups = System.IO.Path.Combine(root, "Profile Groups");
            if (Directory.Exists(groups)) configs.AddRange(Directory.EnumerateFiles(groups, "profiles.ini", SearchOption.AllDirectories));
            foreach (var config in configs.Where(File.Exists))
            {
                var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); string sectionName = "";
                void Add()
                {
                    if (!sectionName.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) || !section.TryGetValue("Path", out var path)) return;
                    var directory = section.GetValueOrDefault("IsRelative", "1") == "1" ? System.IO.Path.GetFullPath(path, System.IO.Path.GetDirectoryName(config)!) : path;
                    var database = System.IO.Path.Combine(directory, "places.sqlite");
                    if (File.Exists(database) && !result.Any(p => p.Path.Equals(database, StringComparison.OrdinalIgnoreCase))) result.Add(new(section.GetValueOrDefault("Name", System.IO.Path.GetFileName(directory)), database));
                }
                foreach (var line in File.ReadLines(config))
                {
                    token.ThrowIfCancellationRequested(); var text = line.Trim();
                    if (text.StartsWith('[') && text.EndsWith(']')) { Add(); section.Clear(); sectionName = text[1..^1]; }
                    else if (!text.StartsWith(';') && text.IndexOf('=') is var equals && equals > 0) section[text[..equals].Trim()] = text[(equals + 1)..].Trim();
                }
                Add();
            }
        }
        return result;
    }
    public static BookmarkNode Read(BookmarkBrowser browser, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return browser == BookmarkBrowser.Firefox ? ReadFirefox(path, token) : ReadChromium(path, token);
    }
    static BookmarkNode ReadChromium(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 });
        if (!document.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object) throw new InvalidDataException("This is not a browser bookmark file.");
        BookmarkNode Parse(JsonElement value, int depth)
        {
            token.ThrowIfCancellationRequested(); if (depth > 64) throw new InvalidDataException("Bookmark folders are nested too deeply.");
            var type = value.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            var node = new BookmarkNode { Name = value.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled" : "Untitled", Folder = type == "folder", Url = value.TryGetProperty("url", out var url) ? url.GetString() : null };
            if (string.IsNullOrWhiteSpace(node.Name)) node.Name = node.Folder ? "Untitled folder" : node.Url ?? "Separator";
            if (node.Folder && value.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) node.Children.Add(Parse(child, depth + 1));
            return node;
        }
        var result = new BookmarkNode { Name = "Bookmarks", Folder = true };
        foreach (var entry in roots.EnumerateObject()) if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("type", out _)) result.Children.Add(Parse(entry.Value, 0));
        token.ThrowIfCancellationRequested(); return result;
    }
    static BookmarkNode ReadFirefox(string path, CancellationToken token)
    {
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Homepage-bookmarks-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3 }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            { source.Open(); destination.Open(); token.ThrowIfCancellationRequested(); source.BackupDatabase(destination); }
            token.ThrowIfCancellationRequested();
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); snapshot.Open();
            using var command = snapshot.CreateCommand();
            command.CommandText = "SELECT b.id,b.parent,b.position,b.title,b.type,p.url,b.guid FROM moz_bookmarks b LEFT JOIN moz_places p ON p.id=b.fk ORDER BY b.parent,b.position,b.id";
            using var reader = command.ExecuteReader();
            var records = new Dictionary<long, (long Parent, BookmarkNode Node, string Guid)>();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var url = reader.IsDBNull(5) ? null : reader.GetString(5);
                var name = reader.IsDBNull(3) ? "" : reader.GetString(3);
                bool folder = reader.GetInt32(4) == 2;
                var guid = reader.IsDBNull(6) ? "" : reader.GetString(6);
                if (string.IsNullOrWhiteSpace(name)) name = guid switch { "toolbar_____" => "Bookmarks toolbar", "menu________" => "Bookmarks menu", "unfiled_____" => "Other bookmarks", "mobile______" => "Mobile bookmarks", _ => folder ? "Untitled folder" : url ?? "Separator" };
                records[reader.GetInt64(0)] = (reader.GetInt64(1), new() { Name = name, Folder = folder, Url = url }, guid);
            }
            var result = new BookmarkNode { Name = "Bookmarks", Folder = true };
            foreach (var record in records.Values)
            {
                if (record.Guid is "root________" or "tags________") continue;
                if (records.TryGetValue(record.Parent, out var parent) && parent.Guid != "root________") parent.Node.Children.Add(record.Node);
                else result.Children.Add(record.Node);
            }
            // Validate the tree before handing it to the UI, including malformed/cyclic databases.
            var seen = new HashSet<BookmarkNode>();
            void Validate(BookmarkNode node, int depth) { if (depth > 64 || !seen.Add(node)) throw new InvalidDataException("Invalid bookmark folder tree."); foreach (var child in node.Children) Validate(child, depth + 1); }
            Validate(result, 0); token.ThrowIfCancellationRequested(); return result;
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(temporary + suffix); } catch (IOException) { } } }
    }
    public static Group MakeGroup(BookmarkNode folder, double x, double y)
    {
        GroupTab Tab(BookmarkNode node, string? name = null) => new() { Name = name ?? node.Name, Rows = true, Scroll = true,
            Items = node.Children.Where(n => n.Supported).Select(MakeShortcut).ToList(), Children = node.Children.Where(n => n.Folder).Select(n => Tab(n)).ToList() };
        var main = Tab(folder, "Main"); var children = main.Children; main.Children = [];
        var group = new Group { Label = folder.Name, X = x, Y = y, Width = 380, Height = 280, Tabs = new[] { main }.Concat(children).ToList(), SelectedTabId = main.Id };
        return group;
    }
    public static Shortcut MakeShortcut(BookmarkNode node) => new() { Label = node.Name, Target = node.Url!, CustomLabel = true };
}
