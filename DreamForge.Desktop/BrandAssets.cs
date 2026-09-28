namespace DreamForge.Desktop;

/// <summary>Loads the product logo that ships next to the executable.</summary>
internal static class BrandAssets
{
    private const string LogoFileName = "YeeYeeYee-logo.png";

    private static readonly Lazy<Image?> sharedLogo = new(LoadLogo);
    private static readonly Lazy<Image?> sharedLogoMark = new(LoadLogoMark);

    /// <summary>Gets the full square logo artwork, or <c>null</c> when the asset is unavailable.</summary>
    public static Image? Logo => sharedLogo.Value;

    /// <summary>
    /// Gets the logo cropped to its visible content, so callers can render the lockup
    /// at a legible size without the empty canvas margin around it.
    /// </summary>
    public static Image? LogoMark => sharedLogoMark.Value;

    private static Image? LoadLogo()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", LogoFileName);
        if (!File.Exists(path)) return null;
        try
        {
            // Read through a stream so the file is not locked for the lifetime of the process.
            using var stream = File.OpenRead(path);
            return Image.FromStream(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static Image? LoadLogoMark()
    {
        var source = Logo;
        if (source is not Bitmap bitmap) return null;
        try
        {
            var bounds = ContentBounds(bitmap);
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            var cropped = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(cropped))
            {
                graphics.DrawImage(bitmap, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
            }
            return cropped;
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Creates a control that renders the logo, falling back to a text wordmark.</summary>
    public static Control CreateMark(Size size, Font fallbackFont, Color fallbackColor)
    {
        var mark = LogoMark;
        if (mark is not null)
        {
            return new PictureBox
            {
                Image = mark,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = size,
                BackColor = Color.Transparent
            };
        }

        return new Label
        {
            Text = "YeeYeeYee",
            AutoSize = true,
            Font = fallbackFont,
            ForeColor = fallbackColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    /// <summary>Finds the bounding box of pixels that differ from the artwork background.</summary>
    private static Rectangle ContentBounds(Bitmap bitmap)
    {
        var background = bitmap.GetPixel(0, 0);
        const int threshold = 26;
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.R - background.R) <= threshold &&
                    Math.Abs(pixel.G - background.G) <= threshold &&
                    Math.Abs(pixel.B - background.B) <= threshold)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0 || maxY < 0) return Rectangle.Empty;

        var padding = Math.Max(4, bitmap.Width / 100);
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(bitmap.Width - 1, maxX + padding);
        maxY = Math.Min(bitmap.Height - 1, maxY + padding);
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
