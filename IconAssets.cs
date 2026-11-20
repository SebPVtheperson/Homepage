using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Homepage;

public sealed class IconAssets(LayoutStore store)
{
    readonly Dictionary<string, ImageSource> images = new(StringComparer.OrdinalIgnoreCase);
    public string StorePng(byte[] png)
    {
        var name = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() + ".png";
        var directory = Path.Combine(store.DataDirectory, "icons"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, png);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temporary); }
        }
        return "icons/" + name;
    }
    public ImageSource For(Shortcut item)
    {
        return (LayoutStore.IsWebsite(item.Target) ? Load(item.WebsiteIcon) : null) ?? Icons.For(store.ResolveTarget(item.Target));
    }
    public ImageSource? Load(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var path = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), store.DataDirectory);
            var iconsDirectory = Path.Combine(store.DataDirectory, "icons") + Path.DirectorySeparatorChar;
            if (!path.StartsWith(iconsDirectory, StringComparison.OrdinalIgnoreCase)) return null;
            if (images.TryGetValue(path, out var image)) return image;
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 128; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            return images[path] = bitmap;
        }
        catch { return null; }
    }
}
