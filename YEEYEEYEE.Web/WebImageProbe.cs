using System.IO.Compression;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>
/// 把一张图**量**成 <see cref="ImageFacts"/>。判据不在这里——规则在共享的
/// <see cref="TechnicalScreening"/> 里，与桌面端同一份；这里只负责解码与数数。
///
/// Web 进程要能跑在 Linux 容器里，不能依赖 Avalonia / System.Drawing，所以 PNG 用
/// 纯托管代码解（IHDR 尺寸 + zlib 解压 + 反滤波），JPEG / GIF 只读文件头里的尺寸。
/// 读不出像素的格式把 <c>SampledPixels</c> 留 0：Shared 的规则只在有采样时才查「纯色」，
/// 不会把「没量到」当成「合格」，也不会凭空判一张坏图。
/// </summary>
public static class WebImageProbe
{
    private const int SampleSide = 24;

    public static ImageFacts Measure(string path, int expectedShortSide)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (PngSize(bytes) is { } png)
                return PngFacts(bytes, png.Width, png.Height, expectedShortSide);
            if (JpegSize(bytes) is { } jpeg)
                return new ImageFacts(true, jpeg.Width, jpeg.Height, 0, 0, expectedShortSide);
            if (GifSize(bytes) is { } gif)
                return new ImageFacts(true, gif.Width, gif.Height, 0, 0, expectedShortSide);
            return new ImageFacts(false, 0, 0, 0, 0, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or OverflowException or InvalidDataException)
        {
            return new ImageFacts(false, 0, 0, 0, 0, 0);
        }
    }

    private static ImageFacts PngFacts(byte[] bytes, int width, int height, int expectedShortSide) =>
        PngSample.TryRead(bytes, out var sample) switch
        {
            // 色深 / 隔行这类**不支持**的变体不猜像素，但尺寸仍然可信。
            PngSample.ReadStatus.Unsupported => new ImageFacts(true, width, height, 0, 0, expectedShortSide),
            // 正文坏掉（zlib 解不开、扫描线不足）就是真的读不出来。
            PngSample.ReadStatus.Corrupt => new ImageFacts(false, 0, 0, 0, 0, 0),
            _ => new ImageFacts(true, width, height, sample.Sampled, sample.DistinctColors, expectedShortSide)
        };

    private static (int Width, int Height)? PngSize(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature)) return null;
        if (bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R') return null;
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width <= 0 || height <= 0 ? null : (width, height);
    }

    private static (int Width, int Height)? JpegSize(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        var offset = 2;
        while (offset + 1 < bytes.Length)
        {
            if (bytes[offset] != 0xFF) { offset++; continue; }
            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (offset + 1 >= bytes.Length) break;
            var length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) break;
            // SOF0–SOF15，排除 DHT(C4) / JPG(C8) / DAC(CC)。
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC && length >= 7)
            {
                var height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                var width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                return width <= 0 || height <= 0 ? null : (width, height);
            }
            if (marker == 0xDA) break;
            offset += length;
        }
        return null;
    }

    private static (int Width, int Height)? GifSize(byte[] bytes)
    {
        if (bytes.Length < 10) return null;
        if (bytes[0] != (byte)'G' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F') return null;
        var width = bytes[6] | (bytes[7] << 8);
        var height = bytes[8] | (bytes[9] << 8);
        return width <= 0 || height <= 0 ? null : (width, height);
    }

    /// <summary>
    /// PNG 的 24×24 采样。只支持 8 位深、非隔行——出图接口给的基本都是这一类；
    /// 其它变体不猜像素，只保留文件头量到的尺寸（见上面的说明）。
    /// </summary>
    private static class PngSample
    {
        public enum ReadStatus { Ok, Unsupported, Corrupt }

        public readonly record struct Result(int Sampled, int DistinctColors);

        public static ReadStatus TryRead(byte[] bytes, out Result result)
        {
            result = default;
            var offset = 8;
            var width = 0;
            var height = 0;
            var bitDepth = 0;
            var colorType = 0;
            var interlace = 0;
            byte[]? palette = null;
            using var compressed = new MemoryStream();
            var sawHeader = false;

            while (offset + 8 <= bytes.Length)
            {
                var length = (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
                if (length < 0 || offset + 12 + length > bytes.Length) return ReadStatus.Corrupt;
                var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
                var data = offset + 8;
                if (type == "IHDR" && length >= 13)
                {
                    width = (bytes[data] << 24) | (bytes[data + 1] << 16) | (bytes[data + 2] << 8) | bytes[data + 3];
                    height = (bytes[data + 4] << 24) | (bytes[data + 5] << 16) | (bytes[data + 6] << 8) | bytes[data + 7];
                    bitDepth = bytes[data + 8];
                    colorType = bytes[data + 9];
                    interlace = bytes[data + 12];
                    sawHeader = true;
                }
                else if (type == "PLTE")
                {
                    palette = bytes.AsSpan(data, length).ToArray();
                }
                else if (type == "IDAT")
                {
                    compressed.Write(bytes, data, length);
                }
                else if (type == "IEND")
                {
                    break;
                }
                offset += 12 + length;
            }

            if (!sawHeader || width <= 0 || height <= 0) return ReadStatus.Corrupt;
            if (bitDepth != 8 || interlace != 0) return ReadStatus.Unsupported;
            var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0) return ReadStatus.Unsupported;
            if (colorType == 3 && (palette is null || palette.Length < 3)) return ReadStatus.Corrupt;

            byte[] raw;
            try
            {
                compressed.Position = 0;
                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                raw = output.ToArray();
            }
            catch (InvalidDataException) { return ReadStatus.Corrupt; }

            var stride = width * channels;
            if (raw.Length < (stride + 1) * height) return ReadStatus.Corrupt;

            var image = new byte[stride * height];
            var source = 0;
            for (var y = 0; y < height; y++)
            {
                var filter = raw[source++];
                var rowStart = y * stride;
                var previous = rowStart - stride;
                for (var x = 0; x < stride; x++)
                {
                    var value = raw[source++];
                    var left = x >= channels ? image[rowStart + x - channels] : (byte)0;
                    var up = y > 0 ? image[previous + x] : (byte)0;
                    var upLeft = x >= channels && y > 0 ? image[previous + x - channels] : (byte)0;
                    image[rowStart + x] = filter switch
                    {
                        0 => value,
                        1 => (byte)(value + left),
                        2 => (byte)(value + up),
                        3 => (byte)(value + ((left + up) >> 1)),
                        4 => (byte)(value + Paeth(left, up, upLeft)),
                        _ => value
                    };
                }
            }

            var colors = new HashSet<int>();
            var sampled = 0;
            for (var sy = 0; sy < SampleSide; sy++)
            {
                var y = sy * height / SampleSide;
                for (var sx = 0; sx < SampleSide; sx++)
                {
                    var x = sx * width / SampleSide;
                    var index = y * stride + x * channels;
                    byte r, g, b;
                    switch (colorType)
                    {
                        case 0: r = g = b = image[index]; break;
                        case 2: r = image[index]; g = image[index + 1]; b = image[index + 2]; break;
                        case 3:
                            var paletteIndex = image[index] * 3;
                            r = palette![paletteIndex]; g = palette[paletteIndex + 1]; b = palette[paletteIndex + 2];
                            break;
                        case 4: r = g = b = image[index]; break;
                        default: r = image[index]; g = image[index + 1]; b = image[index + 2]; break;
                    }
                    sampled++;
                    colors.Add((r >> 4) | ((g >> 4) << 4) | ((b >> 4) << 8));
                }
            }

            result = new Result(sampled, colors.Count);
            return ReadStatus.Ok;

            static int Paeth(int left, int up, int upLeft)
            {
                var estimate = left + up - upLeft;
                var leftDistance = Math.Abs(estimate - left);
                var upDistance = Math.Abs(estimate - up);
                var upLeftDistance = Math.Abs(estimate - upLeft);
                if (leftDistance <= upDistance && leftDistance <= upLeftDistance) return left;
                return upDistance <= upLeftDistance ? up : upLeft;
            }
        }
    }
}
