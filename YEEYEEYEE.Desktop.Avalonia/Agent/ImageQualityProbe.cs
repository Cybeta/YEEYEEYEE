using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 把一张图**量**成 <see cref="ImageFacts"/>。一条判断逻辑都不在这里——判据在
/// <see cref="TechnicalScreening"/> 里，那边没有图片依赖、所以可测；
/// 这边只负责解码与数数，因为只有界面层能解码图片。
/// </summary>
internal static class ImageQualityProbe
{
    /// <summary>采样边长。24 足够看出「整张是不是一个颜色」，也几乎不花时间。</summary>
    private const int SampleSide = 24;

    public static ImageFacts Measure(string path, int expectedShortSide)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sample = Bitmap.DecodeToWidth(stream, SampleSide);
            var size = sample.PixelSize;
            if (size.Width <= 0 || size.Height <= 0) return new ImageFacts(false, 0, 0, 0, 0, 0);

            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                sample.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                    handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            // 解码出来是 Bgra8888。每个通道取高 4 位并成一个桶：这里只关心「整张是不是一个颜色」，
            // 不需要精确的颜色数，量级对了就够（精确数反而会把压缩噪声算成「有很多颜色」）。
            var buckets = new HashSet<int>();
            var sampled = 0;
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                sampled++;
                buckets.Add((buffer[i] >> 4) | ((buffer[i + 1] >> 4) << 4) | ((buffer[i + 2] >> 4) << 8));
            }

            // 真实尺寸：采样出来的宽高是**缩过的**，不能拿来比尺寸，所以另读一次文件头。
            var real = ReadPngSize(path);
            return real is { } png
                ? new ImageFacts(true, png.Width, png.Height, sampled, buckets.Count, expectedShortSide)
                // 读不到原始尺寸（不是 PNG）就不检查尺寸那一条——不猜。
                : new ImageFacts(true, size.Width, size.Height, sampled, buckets.Count, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or OverflowException)
        {
            return new ImageFacts(false, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>
    /// 从 PNG 文件头读原始尺寸：前 24 字节就够（8 字节签名 + 4 字节块长 + 4 字节 "IHDR" + 宽 + 高，后两个是大端）。
    ///
    /// 为什么只为 PNG 做这件事：出图落盘的基本都是 PNG（接口给的就是 PNG 字节），而 JPEG / WebP 要扫段才能定位尺寸，
    /// 为一条不常触发的检查不值得。读不到就是「不检查」，不是「当成合格」——两者在 <see cref="ImageFacts"/> 里分得开
    /// （<c>ExpectedShortSide = 0</c> 表示这条不查）。
    /// </summary>
    private static (int Width, int Height)? ReadPngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < header.Length) return null;

        // PNG 的 8 字节签名：89 50 4E 47 0D 0A 1A 0A
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!header[..8].SequenceEqual(signature)) return null;
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
            return null;

        var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return width <= 0 || height <= 0 ? null : (width, height);
    }
}
