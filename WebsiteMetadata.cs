using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using AngleSharp.Html.Parser;

namespace Homepage;

public sealed record WebsiteDetails(string? Title, byte[]? IconPng, bool PageRead);
public sealed record PageMetadata(string? Title, IReadOnlyList<string> Icons);

public sealed class WebsiteMetadataService : IDisposable
{
    readonly HttpClient client;
    readonly SemaphoreSlim concurrent = new(4);
    readonly ConcurrentDictionary<string, SemaphoreSlim> pages = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> sites = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> iconUrls = new();
    readonly WebsiteCache cache;
    readonly TimeSpan requestTimeout;
    public WebsiteMetadataService(LayoutStore? store = null, HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        cache = new WebsiteCache(store); this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        client = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 6, UseCookies = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Homepage/1.0");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
    public async Task<WebsiteDetails> FetchAsync(string target, CancellationToken cancellation = default, bool refreshTitle = false)
    {
        if (!LayoutStore.IsWebsite(target)) return new(null, null, false);
        await concurrent.WaitAsync(cancellation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(requestTimeout);
        string? title = null; byte[]? icon = null; bool pageRead = false;
        var pageUri = new Uri(target); IReadOnlyList<string> candidates = [new Uri(pageUri, "/favicon.ico").AbsoluteUri];
        try
        {
            // SharePoint documents require a browser session. Do not cache a Microsoft sign-in page as their title or icon.
            if (SharePointMetadata.For(target) is { } sharePoint)
            {
                var saved = cache.GetPage(target);
                title = SharePointMetadata.IsUsefulTitle(saved?.Title, target) ? saved!.Title : sharePoint.Title;
                cache.RememberPage(target, new PageMetadata(title, [SharePointMetadata.IconUrl]));
                var sharedLock = iconUrls.GetOrAdd(SharePointMetadata.IconUrl, _ => new SemaphoreSlim(1));
                await sharedLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    icon = cache.UrlIcon(SharePointMetadata.IconUrl) ?? await SharePointMetadata.LogoAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
                    cache.RememberIcon(WebsiteCache.SiteKey(target), SharePointMetadata.IconUrl, icon);
                }
                finally { sharedLock.Release(); }
                return new(title, icon, false);
            }
            var pageLock = pages.GetOrAdd(WebsiteCache.PageKey(target), _ => new SemaphoreSlim(1));
            await pageLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                var metadata = refreshTitle ? null : cache.GetPage(target);
                if (metadata is null)
                {
                    try
                    {
                        using var response = await client.GetAsync(pageUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode(); pageUri = response.RequestMessage?.RequestUri ?? pageUri;
                        var bytes = await ReadLimited(response.Content, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                        var encoding = Encoding.UTF8;
                        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
                        if (string.IsNullOrWhiteSpace(charset)) charset = Regex.Match(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096)), "charset\\s*=\\s*['\"]?\\s*(?<name>[\\w-]+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Groups["name"].Value;
                        if (!string.IsNullOrWhiteSpace(charset)) { try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { } }
                        metadata = Parse(encoding.GetString(bytes), pageUri); cache.RememberPage(target, metadata);
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException) { }
                }
                if (metadata is not null) { title = metadata.Title; candidates = metadata.Icons; pageRead = true; }
            }
            finally { pageLock.Release(); }
            var siteKey = WebsiteCache.SiteKey(target);
            var siteLock = sites.GetOrAdd(siteKey, _ => new SemaphoreSlim(1));
            await siteLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                icon = cache.SiteIcon(siteKey);
                if (icon is not null) return new(title, icon, pageRead);
                foreach (var candidate in candidates.Take(8))
                {
                    // Different hosts can declare the same CDN icon. Serialize that URL too.
                    var iconLock = iconUrls.GetOrAdd(candidate, _ => new SemaphoreSlim(1));
                    await iconLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                    try
                    {
                        icon = cache.UrlIcon(candidate);
                        if (icon is null)
                        {
                            byte[] bytes;
                            if (candidate.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) bytes = DecodeDataIcon(candidate);
                            else
                            {
                                using var response = await client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                                response.EnsureSuccessStatusCode(); bytes = await ReadLimited(response.Content, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                            }
                            icon = await Icons.NormalizeAsync(bytes).ConfigureAwait(false);
                        }
                        cache.RememberIcon(siteKey, candidate, icon); break;
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { /* Try the next declared icon, then favicon.ico. */ }
                    finally { iconLock.Release(); }
                }
            }
            finally { siteLock.Release(); }
            return new(title, icon, pageRead);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new(title, icon, pageRead); }
        finally { concurrent.Release(); }
    }
    public static PageMetadata Parse(string html, Uri documentUri)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var title = Regex.Replace(document.Title ?? "", "\\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        var baseUri = documentUri;
        var baseHref = document.QuerySelector("head base[href]")?.GetAttribute("href");
        if (Uri.TryCreate(documentUri, baseHref, out var candidateBase) && LayoutStore.IsWebsite(candidateBase.AbsoluteUri)) baseUri = candidateBase;
        var candidates = new List<(string Url, int Rank)>();
        foreach (var link in document.QuerySelectorAll("head link[href]"))
        {
            var rel = (link.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            bool favicon = rel.Contains("icon", StringComparer.OrdinalIgnoreCase);
            if (!favicon && !rel.Any(r => r.StartsWith("apple-touch-icon", StringComparison.OrdinalIgnoreCase))) continue;
            var href = link.GetAttribute("href")!;
            if (href.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) { candidates.Add((href, favicon ? 0 : 10)); continue; }
            if (!Uri.TryCreate(baseUri, href, out var uri) || !LayoutStore.IsWebsite(uri.AbsoluteUri)) continue;
            var sizes = link.GetAttribute("sizes") ?? "";
            int rank = favicon ? 0 : 10;
            if (sizes.Contains("48") || sizes.Contains("64") || sizes.Contains("128") || sizes.Contains("any")) rank--;
            candidates.Add((uri.AbsoluteUri, rank));
        }
        var icons = candidates.OrderBy(c => c.Rank).Select(c => c.Url).Append(new Uri(documentUri, "/favicon.ico").AbsoluteUri).Distinct().ToArray();
        return new(title.Length == 0 ? null : title[..Math.Min(title.Length, 512)], icons);
    }
    static byte[] DecodeDataIcon(string uri)
    {
        int comma = uri.IndexOf(','); if (comma < 0 || uri.Length > 6 * 1024 * 1024) throw new InvalidDataException("Invalid embedded icon.");
        return uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(uri[(comma + 1)..]) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri[(comma + 1)..]));
    }
    static async Task<byte[]> ReadLimited(HttpContent content, int maximum, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Response is too large.");
        using var input = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false); using var output = new MemoryStream();
        var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Response is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public void Dispose() => client.Dispose();
}
