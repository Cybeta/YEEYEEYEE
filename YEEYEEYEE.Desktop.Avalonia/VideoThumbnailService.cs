using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>One process-wide, serial native screenshot page, never the playback page.</summary>
internal sealed class VideoThumbnailService : IDisposable
{
    internal const string CacheVersion = "webview-thumb-v2-short1s-500ms-640x360";
    internal static readonly TimeSpan HostIdleTimeout = TimeSpan.FromSeconds(5);
    internal const long MaxCacheBytes = 256L * 1024 * 1024;
    internal const int MaxCacheEntries = 1024;
    internal static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);
    private int activeRuns;
    private readonly DispatcherTimer idleTimer;
    internal VideoThumbnailService()
    {
        idleTimer = new DispatcherTimer { Interval = HostIdleTimeout };
        idleTimer.Tick += (_, _) =>
        {
            lock (gate)
            {
                if (pending.Count != 0 || activeRuns != 0) return;
                idleTimer.Stop();
                ReleaseHost();
            }
        };
    }
    internal static VideoThumbnailService Shared { get; } = new();
    private readonly object gate = new();
    private readonly Dictionary<string, Work> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private ThumbnailHost? host;
    private bool disposed;
    internal int Captures { get; private set; }
    internal int CacheHits { get; private set; }
    internal int HostsCreated { get; private set; }
    internal string? LastError { get; private set; }
    internal string CacheDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YEEYEEYEE", "VideoThumbnails");
    private sealed class Work
    {
        internal readonly CancellationTokenSource Cancellation;
        internal Task<byte[]?> Task = null!;
        internal int Readers;
        internal bool Finished;
        internal Work(CancellationToken lifetime) => Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }
    internal static string Key(string path)
    {
        var info = new FileInfo(path);
        var identity = OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{CacheVersion}\n{identity}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}")));
    }
    internal async Task<byte[]?> GetAsync(string reference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = LocalVideoPlayer.ValidatePath(reference);
        var key = Key(path);
        Work work;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!pending.TryGetValue(key, out work!))
            {
                work = new Work(lifetime.Token);
                pending.Add(key, work);
                // Do not let synchronous completion remove an entry before its task is assigned.
                work.Task = Task.Run(() => RunAsync(path, key, work));
            }
            work.Readers++;
        }
        try { return await work.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                if (--work.Readers == 0 && !work.Finished)
                {
                    if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, work)) pending.Remove(key);
                    work.Cancellation.Cancel();
                }
            }
        }
    }
    private async Task<byte[]?> RunAsync(string path, string key, Work work)
    {
        var entered = false;
        string? temporary = null;
        try
        {
            var token = work.Cancellation.Token;
            await serial.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            lock (gate) activeRuns++;
            token.ThrowIfCancellationRequested();
            LocalVideoPlayer.ValidatePath(path);
            if (Key(path) != key) return null;
            var cache = Path.Combine(CacheDirectory, key + ".png");
            if (File.Exists(cache))
            {
                try
                {
                    var bytes = new FileInfo(cache).Length <= 2 * 1024 * 1024
                        ? await File.ReadAllBytesAsync(cache, token).ConfigureAwait(false) : [];
                    if (IsPng(bytes))
                    {
                        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow);
                        TrimCache(CacheDirectory, DateTime.UtcNow);
                        CacheHits++;
                        return bytes;
                    }
                    File.Delete(cache);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Cache failures must not prevent decoding. */ }
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    budget.Token.ThrowIfCancellationRequested();
                    idleTimer.Stop();
                    if (host is null) { host = new ThumbnailHost(); HostsCreated++; }
                });
                Captures++;
                var bytes = await host!.CaptureAsync(path, budget.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (Key(path) != key) return null;
                if (!IsPng(bytes)) throw new InvalidDataException("Invalid thumbnail PNG");
                try
                {
                    Directory.CreateDirectory(CacheDirectory);
                    temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, cache, overwrite: true);
                    temporary = null;
                    TrimCache(CacheDirectory, DateTime.UtcNow);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = "Cache: " + error.Message; }
                return bytes;
            }
            catch
            {
                await Dispatcher.UIThread.InvokeAsync(ReleaseHost);
                throw;
            }
        }
        catch (Exception error)
        {
            LastError = error is OperationCanceledException ? "Cancelled or timed out" : error.ToString();
            return null;
        }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            lock (gate)
            {
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, work)) pending.Remove(key);
                work.Finished = true;
                if (entered) activeRuns--;
                work.Cancellation.Dispose();
            }
            if (entered)
            {
                serial.Release();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    lock (gate)
                    {
                        if (!disposed && pending.Count == 0 && activeRuns == 0 && host is not null)
                        {
                            idleTimer.Stop();
                            idleTimer.Start();
                        }
                    }
                });
            }
        }
    }
    // Run only under the serial worker. Modification time records last successful use.
    internal static void TrimCache(string directory, DateTime now, long maxBytes = MaxCacheBytes, int maxEntries = MaxCacheEntries)
    {
        if (!Directory.Exists(directory)) return;
        var entries = new DirectoryInfo(directory).GetFiles("*.png")
            .Where(f => f.Name.Length == 68 && f.Name[..64].All(Uri.IsHexDigit)
                && (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        long bytes = 0;
        int count = 0;
        foreach (var entry in entries)
        {
            if (now - entry.LastWriteTimeUtc <= CacheMaxAge && count < maxEntries && entry.Length <= maxBytes - bytes)
            {
                bytes += entry.Length;
                count++;
                continue;
            }
            try { entry.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retry on next cache use. */ }
        }
        foreach (var entry in new DirectoryInfo(directory).GetFiles("*.tmp"))
        {
            if (entry.Name.Length != 105 || !entry.Name.EndsWith(".tmp", StringComparison.Ordinal)
                || !entry.Name[..64].All(Uri.IsHexDigit) || now - entry.LastWriteTimeUtc < TimeSpan.FromDays(1)) continue;
            try { entry.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    internal static bool IsPng(byte[] bytes)
    {
        if (bytes.Length is <= 24 or > 2 * 1024 * 1024
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        try
        {
            using var bitmap = new global::Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes, writable: false));
            return bitmap.PixelSize.Width is > 0 and <= 640 && bitmap.PixelSize.Height is > 0 and <= 360;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or IOException) { return false; }
    }
    private void ReleaseHost() { idleTimer.Stop(); host?.Dispose(); host = null; }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (gate) { if (disposed) return; disposed = true; lifetime.Cancel(); }
        ReleaseHost();
    }

    private sealed class ThumbnailHost : IDisposable
    {
        private readonly NativeWebView view = new();
        private readonly Window window;
        private LocalVideoPlayer.MediaSession? session;
        private TaskCompletionSource<byte[]>? result;
        private string? request;
        private bool disposed;
        internal ThumbnailHost()
        {
            window = new Window { Width = 640, Height = 360, ShowInTaskbar = false, ShowActivated = false,
                Position = new global::Avalonia.PixelPoint(-10000, -10000), WindowDecorations = WindowDecorations.None, Content = view };
            view.EnvironmentRequested += async (_, args) =>
            {
                if (!OperatingSystem.IsWindows()) return;
                using var deferral = args.GetDeferral();
                try
                {
                    if (args is not global::Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows) throw new NotSupportedException("WebView2 required");
                    windows.ExplicitEnvironment = await SharedWebView2Environment.GetAsync(windows);
                }
                catch (Exception error) { result?.TrySetException(error); }
            };
            view.WebMessageReceived += OnMessage;
            view.NavigationStarted += OnNavigation;
            view.NewWindowRequested += OnNewWindow;
            window.Show();
        }
        internal async Task<byte[]> CaptureAsync(string path, CancellationToken token)
        {
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                result = completion;
                request = Guid.NewGuid().ToString("N");
                session = new LocalVideoPlayer.MediaSession(path, Page(request));
                view.Source = session.PageUri;
            });
            try { return await completion.Task.WaitAsync(token).ConfigureAwait(false); }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    result = null; request = null;
                    session?.Dispose(); session = null;
                    if (!disposed) view.Source = new Uri("about:blank");
                });
            }
        }
        private void OnNavigation(object? sender, WebViewNavigationStartingEventArgs args) => args.Cancel = args.Request?.AbsoluteUri != "about:blank"
            && args.Request?.AbsoluteUri != session?.PageUri.AbsoluteUri;
        private static void OnNewWindow(object? sender, WebViewNewWindowRequestedEventArgs args) => args.Handled = true;
        private void OnMessage(object? sender, WebMessageReceivedEventArgs args)
        {
            if (disposed || !ReferenceEquals(sender, view) || result is null) return;
            try
            {
                var raw = args.Body ?? "";
                if (raw.StartsWith('"')) raw = JsonSerializer.Deserialize<string>(raw) ?? "";
                using var json = JsonDocument.Parse(raw);
                var root = json.RootElement;
                if (root.GetProperty("id").GetString() != request) return;
                if (root.TryGetProperty("error", out var error)) { result.TrySetException(new InvalidOperationException(error.GetString())); return; }
                var data = root.GetProperty("png").GetString() ?? "";
                const string prefix = "data:image/png;base64,";
                if (!data.StartsWith(prefix, StringComparison.Ordinal) || data.Length > 3 * 1024 * 1024) throw new InvalidDataException("Invalid thumbnail payload");
                result.TrySetResult(Convert.FromBase64String(data[prefix.Length..]));
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or InvalidDataException) { result.TrySetException(error); }
        }
        internal static string Page(string id) => """
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; media-src 'self'; script-src 'nonce-thumbnail'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; connect-src 'none'">
            <style>html,body{margin:0;background:#000}video{width:640px;height:360px;object-fit:contain}</style></head><body>
            <video muted playsinline preload="auto" src="media"></video><script nonce="thumbnail">
            const id=__ID__,v=document.querySelector('video');let done=false;
            const send=value=>{if(done)return;done=true;cleanup();const body=JSON.stringify({id,...value});if(typeof window.invokeCSharpAction==='function')window.invokeCSharpAction(body);else if(window.chrome?.webview)window.chrome.webview.postMessage(body);else if(window.webkit?.messageHandlers?.postAvWebViewMessage)window.webkit.messageHandlers.postAvWebViewMessage.postMessage(body)};
            const cleanup=()=>{clearTimeout(timer);v.pause();v.removeAttribute('src');v.load()};
            const timer=setTimeout(()=>send({error:'thumbnail timeout'}),15000);
            window.addEventListener('pagehide',()=>{done=true;cleanup()});
            v.addEventListener('error',()=>{if(!done)send({error:'video decode error '+(v.error?.code??0)})});
            const draw=()=>{if(done)return;try{if(v.readyState<2||!v.videoWidth||!v.videoHeight)throw Error('frame unavailable');const scale=Math.min(640/v.videoWidth,360/v.videoHeight);const c=document.createElement('canvas');c.width=Math.max(1,Math.round(v.videoWidth*scale));c.height=Math.max(1,Math.round(v.videoHeight*scale));c.getContext('2d').drawImage(v,0,0,c.width,c.height);send({png:c.toDataURL('image/png')})}catch(e){send({error:String(e)})}};
            v.addEventListener('loadedmetadata',()=>{if(done)return;if(!Number.isFinite(v.duration)||v.duration<=0){send({error:'invalid duration'});return}const target=v.duration<1?v.duration/2:0.5;v.addEventListener('seeked',draw,{once:true});v.currentTime=target},{once:true});
            </script></body></html>
            """.Replace("__ID__", JsonSerializer.Serialize(id), StringComparison.Ordinal);
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            result?.TrySetCanceled();
            view.WebMessageReceived -= OnMessage;
            view.NavigationStarted -= OnNavigation; view.NewWindowRequested -= OnNewWindow;
            session?.Dispose(); session = null;
            window.Content = null; window.Close();
        }
    }
}
