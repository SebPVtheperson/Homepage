using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Homepage;

public static class WebsiteChecks
{
    public static int Probe(string[] args)
    {
        int index = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(output);
        var requests = new List<string>();
        using var service = new WebsiteMetadataService(new LayoutStore(output), new CountingHandler(requests));
        var results = args.Where(LayoutStore.IsWebsite).Select(url =>
        {
            var details = service.FetchAsync(url).GetAwaiter().GetResult();
            return new { Url = url, details.Title, IconCached = details.IconPng is not null };
        }).ToArray();
        File.WriteAllText(Path.Combine(output, "probe-results.json"), JsonSerializer.Serialize(new { Results = results, Requests = requests }, LayoutStore.JsonOptions));
        return results.All(r => r.Title is not null && r.IconCached) ? 0 : 1;
    }
    sealed class CountingHandler(List<string> requests) : DelegatingHandler(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests) requests.Add(request.RequestUri!.AbsoluteUri);
            return base.SendAsync(request, cancellationToken);
        }
    }
    const string Svg = "<svg xmlns='http://www.w3.org/2000/svg' width='48' height='48' viewBox='0 0 48 48'><rect width='48' height='48' rx='8' fill='#497eab'/><path d='M12 14h24v5H12zm0 10h18v5H12z' fill='white'/></svg>";
    const string SharePointLink = "https://thermofisher.sharepoint.com/sites/MaterialMasterTeam/Shared%20Documents/Forms/AllItems.aspx?FolderCTID=0x01200012D9547EE2CFD241A942A8873632F90B&id=%2Fsites%2FMaterialMasterTeam%2FShared%20Documents%2FGeneral%2FData%20Dictionary";
    public static void Run(Action<bool, string> check, string output)
    {
        var png = Icons.Normalize(Encoding.UTF8.GetBytes(Svg));
        check(png[0] == 137 && png[1] == 80, "Declared SVG favicons convert to portable PNG assets");
        var ico = Ico(png); check(Icons.Normalize(ico).Length > 0, "PNG-backed ICO favicons are supported");
        var unsafeSvg = Svg.Replace("<path", "<image href='https://fixture.invalid/external.png'/><script>alert(1)</script><path");
        check(Icons.Normalize(Encoding.UTF8.GetBytes(unsafeSvg)).SequenceEqual(png), "SVG favicon conversion excludes external images and scripts");
        var page = WebsiteMetadataService.Parse("<head><!-- <title>wrong</title> --><script>let x='<link rel=icon href=wrong.png>';</script><title>  Workshop &amp; Notes\n Home </title><base href='/assets/'><link href='mark.svg' REL='SHORTCUT ICON' sizes='any'></head>", new Uri("https://fixture.invalid/page"));
        check(page.Title == "Workshop & Notes Home", "Page titles decode entities and normalize whitespace");
        check(page.Icons[0] == "https://fixture.invalid/assets/mark.svg" && !page.Icons.Any(i => i.Contains("wrong")), "Favicon discovery honors base URLs and ignores scripts and comments");
        var calls = new ConcurrentDictionary<string, int>();
        HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var url = request.RequestUri!; calls.AddOrUpdate(url.AbsoluteUri, 1, (_, n) => n + 1);
            if (url.AbsolutePath == "/favicon.ico") return Response(request, ico, "image/x-icon");
            if (url.AbsolutePath == "/mark.svg") return Response(request, Svg, "image/svg+xml");
            if (url.AbsolutePath == "/bad.png") return Response(request, "not an image", "image/png");
            if (url.AbsolutePath == "/fallback") return Response(request, "<title>Fallback page</title><link rel=icon href='/bad.png'>");
            if (url.AbsolutePath == "/redirect")
            {
                var response = Response(request, "<title>Redirected page</title><link rel=icon href='mark.svg'>");
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://redirected.invalid/final/page"); return response;
            }
            if (url.AbsolutePath == "/final/mark.svg") return Response(request, Svg, "image/svg+xml");
            return Response(request, "<title>Page " + url.AbsolutePath.Trim('/') + " &amp; workspace</title><link rel=icon href='https://icons.fixture.invalid/mark.svg' sizes='any'>");
        }
        var cacheStore = new LayoutStore(Path.Combine(output, "website-cache"));
        using (var service = new WebsiteMetadataService(cacheStore, new Handler((r, _) => Task.FromResult(Reply(r)))))
        {
            var first = service.FetchAsync("https://app.smartsheet.fixture.invalid/one").GetAwaiter().GetResult();
            var second = service.FetchAsync("https://app.smartsheet.fixture.invalid/two").GetAwaiter().GetResult();
            check(first.Title == "Page one & workspace" && second.Title == "Page two & workspace", "Different links on one website receive their own page titles");
            check(first.IconPng is not null && second.IconPng is not null && calls["https://icons.fixture.invalid/mark.svg"] == 1, "Adding a second link on the same host does not download its favicon again");
            var parallel = Task.WhenAll(Enumerable.Range(0, 8).Select(i => service.FetchAsync("https://parallel.fixture.invalid/" + i))).GetAwaiter().GetResult();
            check(parallel.All(d => d.IconPng is not null) && calls["https://icons.fixture.invalid/mark.svg"] == 1, "Concurrent links and different hosts reuse a shared CDN favicon download");
            var before = calls.Values.Sum();
            service.FetchAsync("https://app.smartsheet.fixture.invalid/one#section").GetAwaiter().GetResult();
            check(calls.Values.Sum() == before, "Repeated links and fragment variants reuse cached titles and icons");
            service.FetchAsync("https://app.smartsheet.fixture.invalid/one", refreshTitle: true).GetAwaiter().GetResult();
            check(calls["https://app.smartsheet.fixture.invalid/one"] == 2 && calls["https://icons.fixture.invalid/mark.svg"] == 1, "Refreshing a page title retains the downloaded favicon");
        }
        check(Directory.GetFiles(Path.Combine(cacheStore.DataDirectory, "icons"), "*.png").Length == 1, "Identical favicons occupy one cached file");
        using (var restarted = new WebsiteMetadataService(cacheStore, new Handler((_, _) => throw new HttpRequestException("Offline"))))
        {
            var details = restarted.FetchAsync("https://app.smartsheet.fixture.invalid/one").GetAwaiter().GetResult();
            check(details.Title == "Page one & workspace" && details.IconPng is not null, "Page titles and favicons remain available offline after restart");
        }
        var restartCalls = new ConcurrentDictionary<string, int>();
        using (var restarted = new WebsiteMetadataService(cacheStore, new Handler((r, _) =>
        {
            restartCalls.AddOrUpdate(r.RequestUri!.AbsoluteUri, 1, (_, n) => n + 1); return Task.FromResult(Reply(r));
        })))
        {
            var details = restarted.FetchAsync("https://app.smartsheet.fixture.invalid/three").GetAwaiter().GetResult();
            check(details.Title == "Page three & workspace" && details.IconPng is not null && restartCalls.Count == 1, "New links after restart fetch only the page, reusing the site's saved icon");
        }
        using (var service = new WebsiteMetadataService(handler: new Handler((r, _) => Task.FromResult(Reply(r)))))
        {
            var details = service.FetchAsync("https://fallback.fixture.invalid/fallback").GetAwaiter().GetResult();
            check(details.Title == "Fallback page" && details.IconPng is not null, "Invalid declared favicon data falls back to the site's root ICO");
            details = service.FetchAsync("https://start.fixture.invalid/redirect").GetAwaiter().GetResult();
            check(details.Title == "Redirected page" && details.IconPng is not null, "Relative favicons resolve against the final redirected page URL");
        }
        using (var service = new WebsiteMetadataService(handler: new Handler((r, _) => Task.FromResult(Response(r, "<title>Embedded icon</title><link rel=icon href='data:image/png;base64," + Convert.ToBase64String(png) + "'>")))))
        {
            check(service.FetchAsync("https://embedded.fixture.invalid/page").GetAwaiter().GetResult().IconPng is not null, "Embedded favicon data URIs are supported");
        }
        using (var service = new WebsiteMetadataService(handler: new Handler((_, _) => throw new HttpRequestException("Offline"))))
        {
            var details = service.FetchAsync("https://offline.fixture.invalid/page").GetAwaiter().GetResult();
            check(details.Title is null && details.IconPng is null && !details.PageRead, "Unavailable websites return a usable fallback without throwing");
        }
        using (var service = new WebsiteMetadataService(handler: new Handler(async (r, token) =>
        {
            if (r.RequestUri!.AbsolutePath == "/page") return Response(r, "<title>Title before timeout</title><link rel=icon href='/slow.ico'>");
            await Task.Delay(10000, token).ConfigureAwait(false); return new HttpResponseMessage(HttpStatusCode.NotFound);
        }), requestTimeout: TimeSpan.FromMilliseconds(150)))
        {
            var details = service.FetchAsync("https://timeout.fixture.invalid/page").GetAwaiter().GetResult();
            check(details.Title == "Title before timeout" && details.PageRead, "An icon timeout preserves a successfully fetched page title");
        }
        var sharePointStore = new LayoutStore(Path.Combine(output, "sharepoint")); int privateRequests = 0;
        var oldCache = new WebsiteCache(sharePointStore);
        oldCache.RememberPage(SharePointLink, new("Sign in to your account", ["https://login.microsoftonline.com/favicon.ico"]));
        oldCache.RememberIcon(WebsiteCache.SiteKey(SharePointLink), "https://login.microsoftonline.com/favicon.ico", png);
        using (var service = new WebsiteMetadataService(sharePointStore, new Handler((_, _) => { privateRequests++; throw new HttpRequestException("Sign-in required"); })))
        {
            var details = service.FetchAsync(SharePointLink).GetAwaiter().GetResult();
            check(details.Title == "Material Master Team - Documents" && details.IconPng is not null, "Authenticated SharePoint links get their site/library title and official SharePoint logo");
            check(!details.PageRead && privateRequests == 0, "SharePoint fallback works without accessing a browser session or downloading a sign-in page");
            check(!details.IconPng!.SequenceEqual(png), "Old cached Microsoft sign-in icons are replaced by the SharePoint logo");
            var another = service.FetchAsync(SharePointLink + "&view=another").GetAwaiter().GetResult();
            check(details.IconPng.SequenceEqual(another.IconPng!) && privateRequests == 0, "Additional SharePoint links reuse the same cached logo without downloading it again");
        }
        check(SharePointMetadata.For("https://thermofisher.sharepoint.com/teams/MasterDataQA/Shared%20Documents/Forms/AllItems.aspx")?.Title == "Master Data QA - Documents", "SharePoint names handle teams URLs, encoded spaces, and acronyms");
        check(!SharePointMetadata.IsSharePoint("https://fake-sharepoint.com/site") && !SharePointMetadata.IsSharePoint("https://sharepoint.com.example.invalid/sites/team"), "SharePoint fallback applies only to actual SharePoint tenant hosts");
        RunUi(check, output, png);
        var startupStore = new LayoutStore(Path.Combine(output, "sharepoint-startup"));
        startupStore.Save(new Layout { Items = [new Shortcut { Label = "Sign in to your account", WebsiteTitle = "Sign in to your account", WebsiteMetadataFetched = true, Target = SharePointLink }] });
        var startup = new MainWindow(startupStore) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        startup.Show(); Wait(startup.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);
        check(startup.CurrentLayout.Items[0].Label == "Material Master Team - Documents" && startup.CurrentLayout.Items[0].WebsiteIcon is not null, "Reopening the launcher repairs existing SharePoint shortcuts even if previously marked fetched");
        startup.Close();
    }
    static void RunUi(Action<bool, string> check, string output, byte[] png)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new LayoutStore(Path.Combine(output, "website-ui")); Directory.CreateDirectory(store.BaseDirectory);
        var service = new WebsiteMetadataService(store, new Handler(async (r, token) =>
        {
            if (r.RequestUri!.AbsolutePath == "/delayed") await gate.Task.WaitAsync(token).ConfigureAwait(false);
            return r.RequestUri.AbsolutePath == "/icon.png" ? Response(r, png, "image/png") : Response(r, "<title>Fetched webpage title</title><link rel=icon href='/icon.png'>");
        }));
        var window = new MainWindow(store, true, service) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show();
        var item = new Shortcut { Label = "fixture.invalid", Target = "https://fixture.invalid/page" };
        window.SetLayoutForTest(new Layout { Items = [item] }); window.UpdateLayout();
        Wait(window.RefreshWebsiteAsync(item)); window.UpdateLayout();
        check(item.Label == "Fetched webpage title" && item.WebsiteIcon is not null && item.WebsiteMetadataFetched, "Website tiles automatically adopt fetched titles and favicons");
        item.Label = "My personal label"; item.CustomLabel = true;
        Wait(window.RefreshWebsiteAsync(item, true)); check(item.Label == "My personal label", "Website refresh preserves user-renamed shortcut labels");
        store.Save(window.CurrentLayout); var reloaded = store.Load().Items[0];
        check(reloaded.WebsiteTitle == "Fetched webpage title" && reloaded.WebsiteIcon == item.WebsiteIcon && new IconAssets(store).For(reloaded) is BitmapSource, "Website details and displayed icons survive a layout reload");
        var moved = new LayoutStore(Path.Combine(output, "moved-website-icons")); Directory.CreateDirectory(Path.Combine(moved.DataDirectory, "icons"));
        File.Copy(Path.Combine(store.DataDirectory, item.WebsiteIcon!.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(moved.DataDirectory, item.WebsiteIcon.Replace('/', Path.DirectorySeparatorChar)));
        check(new IconAssets(moved).For(reloaded) is BitmapSource, "Website icons travel with the EXE's data folder");
        var delayed = new Shortcut { Label = "fixture.invalid", Target = "https://fixture.invalid/delayed" }; window.CurrentLayout.Items.Add(delayed); window.Render();
        var pending = window.RefreshWebsiteAsync(delayed);
        var tile = MainWindow.FindDescendants<Border>(window.Surface).First(b => b.Tag as string == delayed.Id);
        var remove = tile.ContextMenu.Items.OfType<MenuItem>().First(m => (string)m.Header == "Remove shortcut"); remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        window.Undo(); gate.SetResult(); Wait(pending);
        check(window.CurrentLayout.Items.First(i => i.Id == delayed.Id).WebsiteTitle is null, "Delayed website fetches cannot overwrite a layout restored by undo");
        window.Close();
    }
    static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    static byte[] Ico(byte[] png)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)128); writer.Write((byte)128); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(png.Length); writer.Write(22); writer.Write(png);
        return stream.ToArray();
    }
    static HttpResponseMessage Response(HttpRequestMessage request, string text, string type = "text/html") => Response(request, Encoding.UTF8.GetBytes(text), type);
    static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type); return response;
    }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}
