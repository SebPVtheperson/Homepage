using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Homepage;

public static class Icons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellInfo info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    static readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource For(string path)
    {
        if (LayoutStore.IsWebsite(path)) return Globe;
        if (cache.TryGetValue(path, out var image)) return image;
        var info = new ShellInfo();
        try
        {
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100);
            if (info.Icon != IntPtr.Zero)
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
                bitmap.Freeze(); return cache[path] = bitmap;
            }
        }
        catch { /* A generic icon remains usable when shell lookup fails. */ }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
        return FileIcon;
    }
    public static readonly ImageSource Globe = Draw(true);
    public static readonly ImageSource FileIcon = Draw(false);
    public static byte[] Normalize(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Choose an icon smaller than 4 MB.");
        ImageSource image;
        // Raster files go through WIC; SVG is converted to a drawing without external resources.
        try
        {
            using var input = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) throw new InvalidDataException("Choose an image no larger than 4096 × 4096 pixels.");
            image = frame;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException)
        {
            image = ReadSvg(bytes);
        }
        return ToPng(image);
    }
    public static byte[] ToPng(ImageSource image)
    {
        var visual = new DrawingVisual();
        double width = image.Width, height = image.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new InvalidDataException("The icon has no visible image.");
        var scale = Math.Min(128 / width, 128 / height);
        var rect = new Rect((128 - width * scale) / 2, (128 - height * scale) / 2, width * scale, height * scale);
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(image, rect);
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    public static Task<byte[]> NormalizeAsync(byte[] bytes)
    {
        var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => { try { result.SetResult(Normalize(bytes)); } catch (Exception error) { result.SetException(error); } }) { IsBackground = true, Name = "Homepage icon decoder" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); return result.Task;
    }
    static ImageSource ReadSvg(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name.LocalName != "svg") throw new InvalidDataException("Choose a supported image or SVG icon.");
        var allowed = new HashSet<string>(["svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "defs", "clipPath", "mask", "linearGradient", "radialGradient", "stop", "use", "title", "desc", "style"], StringComparer.Ordinal);
        foreach (var node in xml.Root.Descendants().ToArray())
        {
            if (!allowed.Contains(node.Name.LocalName)) { node.Remove(); continue; }
            if (node.Name.LocalName == "style" && ExternalCss(node.Value)) node.Remove();
        }
        foreach (var node in xml.Root.DescendantsAndSelf())
        {
            foreach (var attribute in node.Attributes().ToArray())
            {
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Name.LocalName is "base" ||
                    (attribute.Name.LocalName is "href" && !attribute.Value.StartsWith('#')) || ExternalCss(attribute.Value)) attribute.Remove();
            }
        }
        using var safe = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
        var converter = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true }) { SaveXaml = false, SaveZaml = false };
        var drawing = converter.Read(safe) ?? throw new InvalidDataException("This SVG could not be read.");
        return new DrawingImage(drawing);
    }
    static bool ExternalCss(string value) => value.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
        Regex.Matches(value, "url\\(\\s*['\"]?(?<target>[^)'\"]+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Any(m => !m.Groups["target"].Value.Trim().StartsWith('#'));
    static ImageSource Draw(bool globe)
    {
        var drawing = new DrawingGroup();
        using (var c = drawing.Open())
        {
            var blue = new SolidColorBrush(Color.FromRgb(91, 128, 155));
            var pen = new Pen(blue, 1.8);
            if (globe)
            {
                c.DrawEllipse(new SolidColorBrush(Color.FromRgb(233, 241, 246)), pen, new Point(24, 24), 18, 18);
                c.DrawEllipse(null, pen, new Point(24, 24), 8, 18);
                c.DrawLine(pen, new Point(6, 24), new Point(42, 24));
                c.DrawLine(pen, new Point(9, 15), new Point(39, 15));
                c.DrawLine(pen, new Point(9, 33), new Point(39, 33));
            }
            else
            {
                c.DrawRoundedRectangle(Brushes.White, pen, new Rect(11, 5, 27, 38), 3, 3);
                for (int y = 17; y < 35; y += 6) c.DrawLine(pen, new Point(17, y), new Point(32, y));
            }
        }
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
}
