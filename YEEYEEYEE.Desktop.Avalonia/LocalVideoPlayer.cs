using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>A lazy, single-session local video surface. Detaching destroys the native adapter.</summary>
internal sealed class LocalVideoPlayer : ContentControl, IDisposable
{
    private static LocalVideoPlayer? active;
    private readonly string reference;
    private readonly Func<bool>? canStartInline;
    private readonly Action? openLargePreview;
    private DispatcherTimer? pendingPlay;
    private long suppressPlayUntil;
    private NativeWebView? webView;
    private MediaSession? session;
    private string? previewMessage;
    private bool disposed;
    private readonly Stretch posterStretch;
    private Bitmap? firstFrame;
    private string? idleHint;
    private static readonly object frameCacheGate = new();
    // Cache encoded thumbnails, not shared disposable bitmaps. Rebuilt canvas tiles own their copies.
    private static readonly Dictionary<string, Task<byte[]?>> frameCache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim frameExtractionSlots = new(2);

    public LocalVideoPlayer(string reference, Func<bool>? canStartInline = null,
        Stretch posterStretch = Stretch.Uniform, Action? openLargePreview = null)
    {
        this.reference = reference;
        this.canStartInline = canStartInline;
        this.posterStretch = posterStretch;
        this.openLargePreview = openLargePreview;
        if (openLargePreview is not null)
        {
            // Delay the idle play action so the native child does not replace the poster
            // between the two clicks of a double tap.
            AddHandler(TappedEvent, (_, args) =>
            {
                args.Handled = true;
                if (webView is null) SchedulePlay();
            }, global::Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(DoubleTappedEvent, (_, args) =>
            {
                args.Handled = true;
                CancelPendingPlay();
                suppressPlayUntil = Environment.TickCount64 + 500;
                openLargePreview();
            }, global::Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }
        ShowPlayButton();
        DetachedFromVisualTree += (_, _) => Dispose();
        _ = LoadFirstFrameAsync();
    }

    private async Task LoadFirstFrameAsync()
    {
        try
        {
            var bytes = await Task.Run(async () =>
            {
                // Apply the same local-file boundary checks before invoking the decoder.
                var path = ValidatePath(reference);
                var info = new FileInfo(path);
                var key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
                Task<byte[]?> extraction;
                lock (frameCacheGate)
                {
                    if (!frameCache.TryGetValue(key, out extraction!)
                        || extraction.IsCompleted && (!extraction.IsCompletedSuccessfully || extraction.Result is null))
                    {
                        foreach (var expired in frameCache.Where(pair => pair.Value.IsCompleted
                            && (frameCache.Count >= 160 || pair.Value.IsCompletedSuccessfully && pair.Value.Result is null))
                            .Select(pair => pair.Key).ToList())
                            frameCache.Remove(expired);
                        frameCache[key] = extraction = ExtractFirstFrameAsync(path);
                    }
                }
                return await extraction.ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (bytes is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (disposed) return;
                using var stream = new MemoryStream(bytes, writable: false);
                firstFrame = new Bitmap(stream);
                if (webView is null) ShowPlayButton(idleHint);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or Win32Exception)
        {
            // Missing decoder, invalid file or unsupported codec: keep the explicit play button.
        }
    }

    private static async Task<byte[]?> ExtractFirstFrameAsync(string path)
    {
        await frameExtractionSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            // Revalidate after waiting; never accept a nearby role/reference image as a poster.
            path = ValidatePath(path);
            // Windows releases ship the tested decoder and its license together. Never
            // silently select an unrelated/cut-down executable from the user's PATH.
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "win-x64", "ffmpeg.exe")
                : "ffmpeg";
            if (OperatingSystem.IsWindows() && !File.Exists(executable)) return null;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin",
                "-protocol_whitelist", "file,pipe", "-i", path, "-map", "0:v:0", "-frames:v", "1",
                "-vf", "scale=640:360:force_original_aspect_ratio=decrease", "-threads", "1",
                "-f", "image2pipe", "-vcodec", "png", "pipe:1" })
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            process.Start();
            try
            {
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                using var output = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)
                    .ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > 2 * 1024 * 1024) return null;
                    output.Write(buffer, 0, read);
                }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                return process.ExitCode == 0 && output.Length > 0 ? output.ToArray() : null;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            return null;
        }
        finally { frameExtractionSlots.Release(); }
    }

    private void ShowPlayButton(string? hint = null)
    {
        idleHint = hint;
        var face = new Panel { ClipToBounds = true };
        if (firstFrame is not null)
            face.Children.Add(new Image { Source = firstFrame, Stretch = posterStretch });
        var play = new Button
        {
            Content = "▶ 播放",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        play.Click += (_, args) =>
        {
            args.Handled = true;
            if (openLargePreview is null) StartFromUserAction();
            else SchedulePlay();
        };
        if (hint is null)
        {
            face.Children.Add(play);
            Content = face;
            return;
        }
        face.Children.Add(new StackPanel
        {
            Spacing = 4,
            Margin = new global::Avalonia.Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = hint,
                    FontSize = 11,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    TextAlignment = global::Avalonia.Media.TextAlignment.Center
                },
                play
            }
        });
        Content = face;
    }

    internal static string ValidatePath(string reference)
    {
        string path;
        if (reference.StartsWith("asset://", StringComparison.Ordinal))
        {
            var name = reference[8..];
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
                || name.IndexOfAny(['/', '\\', ':', '%', '?', '#']) >= 0)
                throw new ArgumentException("无效的本地资产引用");
            path = Path.Combine(AssetStore.Directory, name);
        }
        else path = reference;

        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && (path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/')
                || path[2..].Contains(':'))))
            throw new ArgumentException("仅支持本机绝对视频路径，不支持网络、设备或流路径");

        path = Path.GetFullPath(path);
        if (!File.Exists(path) || WorkflowAttachment.KindOf(path) != AttachmentKind.Video)
            throw new IOException("视频文件不存在或类型不受支持");
        for (var entry = path; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不允许通过符号链接或重解析点播放视频");
        using var readable = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return path;
    }

    private void CancelPendingPlay()
    {
        pendingPlay?.Stop();
        pendingPlay = null;
    }

    private void SchedulePlay()
    {
        if (disposed || webView is not null || pendingPlay is not null
            || Environment.TickCount64 < suppressPlayUntil) return;
        pendingPlay = new DispatcherTimer
        {
            Interval = global::Avalonia.Application.Current?.PlatformSettings?
                .GetDoubleTapTime(global::Avalonia.Input.PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500)
        };
        pendingPlay.Tick += (_, _) =>
        {
            CancelPendingPlay();
            StartFromUserAction();
        };
        pendingPlay.Start();
    }

    internal static bool IsPreviewMessage(string? body, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || body is null) return false;
        if (string.Equals(body, expected, StringComparison.Ordinal)) return true;
        // WebView2 returns strings directly; other bridges can return a JSON string.
        // Parse JSON instead of comparing quote spellings (which misses escaped characters).
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.String
                && string.Equals(json.RootElement.GetString(), expected, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        var source = webView;
        var playback = session;
        var message = previewMessage;
        // NativeWebView 12.1.0 forwards the adapter event with the control as sender.
        if (disposed || source is null || playback is null || !ReferenceEquals(sender, source)
            || !IsPreviewMessage(args.Body, message)) return;
        Dispatcher.UIThread.Post(() =>
        {
            // A queued event must not act on a stopped/replaced playback instance.
            if (disposed || !ReferenceEquals(webView, source) || !ReferenceEquals(session, playback)
                || !string.Equals(previewMessage, message, StringComparison.Ordinal)) return;
            CancelPendingPlay();
            suppressPlayUntil = Environment.TickCount64 + 500;
            openLargePreview?.Invoke();
        });
    }

    // Call only from an explicit play click or media activation, never selection/attachment.
    public void StartFromUserAction()
    {
        if (disposed || webView is not null) return;
        // Native child windows escape Avalonia clipping. Gate canvas playback before
        // stopping another player, opening a file, or creating any native resources.
        if (canStartInline is not null && !canStartInline())
        {
            ShowPlayButton("预览框未完整显示，请移回画布后播放，或双击视频打开大预览。");
            return;
        }
        active?.Stop();
        try
        {
            var path = ValidatePath(reference);
            previewMessage = "local-video-preview:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            // Both resources use the same loopback origin, created only after a play click.
            var html = """
                <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; media-src 'self'; style-src 'unsafe-inline'; script-src 'nonce-local-video'; base-uri 'none'; form-action 'none'; connect-src 'none'">
                <style>
                html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#101010;color:#eee;font:14px sans-serif}
                video{display:block;position:fixed;inset:0;width:100%;height:100%;object-fit:contain}
                #error,#hint{position:fixed;left:16px;right:16px;margin:0;box-sizing:border-box;pointer-events:none;background:#101010cc;padding:8px;line-height:1.4;overflow-wrap:anywhere}
                #error{top:16px;max-height:calc(100% - 32px);overflow:auto;pointer-events:auto}
                #hint{bottom:56px;animation:fadeHint 3.5s forwards}
                @keyframes fadeHint{0%,85%{opacity:1;visibility:visible}100%{opacity:0;visibility:hidden}}
                @media(max-width:319.98px),(max-height:179.98px){#hint{display:none}#error{top:4px;left:4px;right:4px;padding:4px 6px;font-size:12px;line-height:1.3;max-height:calc(100% - 8px)}}
                </style></head><body>
                <video controls muted playsinline preload="auto" controlslist="nodownload noremoteplayback" disablepictureinpicture src="__MEDIA__"></video><p id="error" hidden></p><p id="hint">默认静音播放，可通过下方控制开启声音。</p>
                <script nonce="local-video">
                const v=document.querySelector('video'),e=document.getElementById('error'),h=document.getElementById('hint');let stopped=false;
                v.defaultMuted=true;v.muted=true;
                let lastPreview=-Infinity;
                const preview=event=>{if(!__PREVIEW_ENABLED__||(event.type==='click'&&event.detail!==2))return;event.preventDefault();event.stopImmediatePropagation();if(stopped||event.timeStamp-lastPreview<500)return;lastPreview=event.timeStamp;__PREVIEW__};
                document.addEventListener('click',preview,true);
                document.addEventListener('dblclick',preview,true);
                const updateHint=()=>{h.hidden=!v.muted};
                v.addEventListener('volumechange',updateHint);
                const notice=text=>{e.hidden=false;e.textContent=text};
                const playWhenReady=async()=>{if(stopped)return;v.muted=true;try{await v.play();if(stopped)v.pause()}catch(error){if(stopped)return;v.controls=true;notice(error.name==='NotAllowedError'?'浏览器阻止了自动播放，请点击下方播放按钮。':'无法开始播放，请使用下方控制重试；系统 WebView 可能不支持该编码。')}};
                window.stopVideo=()=>{stopped=true;v.removeEventListener('canplay',playWhenReady);v.pause();v.removeAttribute('src');v.load()};
                window.addEventListener('pagehide',window.stopVideo);
                v.addEventListener('playing',()=>{e.hidden=true;e.textContent='';updateHint()});
                v.addEventListener('error',()=>{if(!stopped)notice('无法播放此视频：文件不可读或系统 WebView 不支持该编码。')});
                v.addEventListener('canplay',playWhenReady,{once:true});if(v.readyState>=3){v.removeEventListener('canplay',playWhenReady);playWhenReady()}
                </script></body></html>
                """.Replace("__MEDIA__", "media", StringComparison.Ordinal)
                .Replace("__PREVIEW_ENABLED__", openLargePreview is null ? "false" : "true", StringComparison.Ordinal)
                .Replace("__PREVIEW__", openLargePreview is null ? string.Empty
                    : "const message=" + JsonSerializer.Serialize(previewMessage) + ";"
                        + "if(typeof window.invokeCSharpAction==='function')window.invokeCSharpAction(message);"
                        + "else if(window.chrome?.webview)window.chrome.webview.postMessage(message);"
                        + "else if(window.webkit?.messageHandlers?.postAvWebViewMessage)window.webkit.messageHandlers.postAvWebViewMessage.postMessage(message);", 
                    StringComparison.Ordinal);
            session = new MediaSession(path, html);
            webView = new NativeWebView();
            webView.NavigationStarted += OnNavigationStarted;
            webView.NewWindowRequested += OnNewWindowRequested;
            webView.WebMessageReceived += OnWebMessageReceived;
            webView.Source = session.PageUri;
            Content = webView;
            active = this;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or HttpListenerException or SocketException)
        {
            Stop();
            ShowPlayButton("视频预览失败：" + error.Message);
        }
    }

    public void Stop()
    {
        CancelPendingPlay();
        var old = webView;
        webView = null;
        previewMessage = null;
        if (ReferenceEquals(active, this)) active = null;
        if (old is not null)
        {
            old.NavigationStarted -= OnNavigationStarted;
            old.NewWindowRequested -= OnNewWindowRequested;
            old.WebMessageReceived -= OnWebMessageReceived;
            _ = StopScriptAsync(old);
            // Do not await JavaScript: removal also cancels pending initialization and destroys the controller.
            Content = null;
        }
        session?.Dispose();
        session = null;
        if (!disposed) ShowPlayButton();
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        // Only this session's exact page URL may be a top-level navigation.
        args.Cancel = session is null || args.Request is not { IsAbsoluteUri: true } uri
            || !string.Equals(uri.AbsoluteUri, session.PageUri.AbsoluteUri, StringComparison.Ordinal);
    }

    private static void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    /// <summary>Owns one pinned file and two token-scoped HTTP resources; never maps URL paths to disk.</summary>
    internal sealed class MediaSession : IDisposable
    {
        private readonly HttpListener listener;
        private readonly FileStream file;
        private readonly byte[] page;
        private readonly string mediaPath;
        private readonly string mime;
        private readonly object gate = new();
        private readonly HashSet<HttpListenerContext> requests = [];
        private bool stopped;
        internal Uri PageUri { get; }

        internal MediaSession(string path, string html)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            page = Encoding.UTF8.GetBytes(html);
            mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => "video/mp4",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                ".wmv" => "video/x-ms-wmv",
                ".mpeg" or ".mpg" => "video/mpeg",
                ".ogv" or ".ogg" => "video/ogg",
                ".3gp" => "video/3gpp",
                _ => "application/octet-stream"
            };
            try
            {
                // HttpListener cannot bind port zero. Retry if another process takes the
                // ephemeral port between the socket probe and the HTTP listener bind.
                for (var attempt = 0; ; attempt++)
                {
                    var probe = new TcpListener(IPAddress.Loopback, 0);
                    int port;
                    try { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
                    finally { probe.Stop(); }
                    var candidate = new HttpListener();
                    var origin = $"http://127.0.0.1:{port}/";
                    candidate.Prefixes.Add(origin);
                    try { candidate.Start(); }
                    catch (HttpListenerException) when (attempt < 7) { candidate.Close(); continue; }
                    catch { candidate.Close(); throw; }
                    listener = candidate;
                    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    PageUri = new Uri(origin + token + "/index.html");
                    mediaPath = "/" + token + "/media";
                    break;
                }
                _ = AcceptAsync();
            }
            catch { file.Dispose(); throw; }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(false);
                    lock (gate)
                    {
                        if (stopped) { context.Response.Abort(); return; }
                        requests.Add(context);
                    }
                    _ = ServeAsync(context);
                }
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                Dispose();
            }
        }

        private async Task ServeAsync(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var request = context.Request;
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["Referrer-Policy"] = "no-referrer";
                // Exact raw targets reject queries, traversal, escaped aliases and unknown tokens.
                if (request.Url?.Authority != PageUri.Authority
                    || (request.RawUrl != PageUri.AbsolutePath && request.RawUrl != mediaPath))
                { response.StatusCode = 404; response.ContentLength64 = 0; return; }
                if (request.HttpMethod is not ("GET" or "HEAD"))
                {
                    response.StatusCode = 405;
                    response.Headers["Allow"] = "GET, HEAD";
                    response.ContentLength64 = 0;
                    return;
                }
                var head = request.HttpMethod == "HEAD";
                if (request.RawUrl == PageUri.AbsolutePath)
                {
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = page.Length;
                    if (!head) await response.OutputStream.WriteAsync(page).ConfigureAwait(false);
                    return;
                }
                long length;
                lock (gate) { if (stopped) return; length = file.Length; }
                response.ContentType = mime;
                response.Headers["Accept-Ranges"] = "bytes";
                long start = 0, end = length - 1;
                // RFC 9110: Range applies to GET only. Unsupported/multiple/malformed
                // ranges are ignored (full 200); a valid unsatisfiable range returns 416.
                if (!head && request.Headers["Range"] is { } range
                    && request.Headers["If-Range"] is null
                    && TryRange(range, length, out var first, out var last, out var unsatisfiable))
                {
                    if (unsatisfiable)
                    {
                        response.StatusCode = 416;
                        response.Headers["Content-Range"] = $"bytes */{length}";
                        response.ContentLength64 = 0;
                        return;
                    }
                    start = first; end = last;
                    response.StatusCode = 206;
                    response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
                }
                response.ContentLength64 = end - start + 1;
                if (head) return;
                var buffer = new byte[64 * 1024];
                var position = start;
                while (position <= end)
                {
                    int read;
                    lock (gate)
                    {
                        if (stopped) return;
                        // Independent offsets allow concurrent seek requests without sharing Position.
                        read = RandomAccess.Read(file.SafeFileHandle, buffer.AsSpan(0,
                            (int)Math.Min(buffer.Length, end - position + 1)), position);
                    }
                    if (read == 0) { response.Abort(); return; }
                    await response.OutputStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    position += read;
                }
            }
            catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException
                or InvalidOperationException) { /* Browser disconnect or session shutdown. */ }
            finally
            {
                lock (gate) requests.Remove(context);
                try { response.Close(); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
            }
        }

        private static bool TryRange(string value, long length, out long start, out long end, out bool unsatisfiable)
        {
            start = 0; end = length - 1; unsatisfiable = false;
            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = value[6..].Trim().Split('-');
            if (parts.Length != 2) return false;
            static bool Number(string text, out long number) => long.TryParse(text,
                NumberStyles.None, CultureInfo.InvariantCulture, out number);
            if (parts[0].Length == 0)
            {
                if (!Number(parts[1], out var suffix)) return false;
                unsatisfiable = suffix == 0 || length == 0;
                start = Math.Max(0, length - suffix);
                return true;
            }
            if (!Number(parts[0], out start)) return false;
            if (parts[1].Length > 0)
            {
                if (!Number(parts[1], out end) || end < start) return false;
                end = Math.Min(end, length - 1);
            }
            unsatisfiable = start >= length;
            return true;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                listener.Close();
                foreach (var context in requests)
                {
                    try { context.Response.Abort(); }
                    catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
                }
                requests.Clear();
                file.Dispose();
            }
        }
    }

    private static async Task StopScriptAsync(NativeWebView view)
    {
        try { await view.InvokeScript("window.stopVideo?.()"); }
        catch (Exception) { /* Detaching may have already destroyed the adapter. */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        Content = null;
        firstFrame?.Dispose();
        firstFrame = null;
    }
}
