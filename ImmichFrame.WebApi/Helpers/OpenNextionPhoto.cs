using SkiaSharp;

namespace ImmichFrame.WebApi.Helpers;

internal enum OpenNextionFit
{
    // Whole photo on black bars.
    Contain,
    // Fill the screen, cropping the edges.
    Cover,
    // Whole photo over a blurred, darkened copy of itself (ImmichFrame's
    // non-filled look).
    Blur
}

// A decoded photo with its EXIF orientation, ready to draw.
internal sealed class OpenNextionSource : IDisposable
{
    public required SKImage Image { get; init; }
    public int Degrees { get; init; }
    public bool Mirrored { get; init; }

    private bool Sideways => Degrees is 90 or 270;
    public int Width => Sideways ? Image.Height : Image.Width;
    public int Height => Sideways ? Image.Width : Image.Height;

    public void Dispose() => Image.Dispose();
}

internal static class OpenNextionPhoto
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly Lazy<SKTypeface> Typeface = new(LoadTypeface);

    // Kept for callers that render one photo without a caption.
    public static async Task<byte[]?> Render(Stream stream, int width, int height, bool cover,
        CancellationToken cancellationToken)
    {
        using var source = await Decode(stream, cancellationToken);
        if (source is null) return null;
        return Compose([source], [[]], width, height, cover ? OpenNextionFit.Cover : OpenNextionFit.Contain);
    }

    public static async Task<OpenNextionSource?> Decode(Stream stream, CancellationToken cancellationToken)
    {
        // Immich may return a non-seekable HTTP stream. Skia needs a rewindable source.
        const int maxPreviewBytes = 16 * 1024 * 1024;
        using var preview = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            if (preview.Length + count > maxPreviewBytes) return null;
            preview.Write(buffer, 0, count);
        }

        preview.Position = 0;
        using var input = new SKManagedStream(preview);
        using var codec = SKCodec.Create(input);
        if (codec is null || (long)codec.Info.Width * codec.Info.Height > 16_000_000)
            return null;

        using var photo = new SKBitmap();
        if (!photo.TryAllocPixels(new SKImageInfo(codec.Info.Width, codec.Info.Height)) ||
            codec.GetPixels(photo.Info, photo.GetPixels()) != SKCodecResult.Success)
            return null;

        // EXIF rotation and mirroring, applied around the photo centre when drawn.
        var (degrees, mirrored) = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.TopRight => (0, true),
            SKEncodedOrigin.BottomRight => (180, false),
            SKEncodedOrigin.BottomLeft => (180, true),
            SKEncodedOrigin.LeftTop => (270, true),
            SKEncodedOrigin.RightTop => (90, false),
            SKEncodedOrigin.RightBottom => (90, true),
            SKEncodedOrigin.LeftBottom => (270, false),
            _ => (0, false)
        };
        var image = SKImage.FromBitmap(photo);
        return image is null ? null : new OpenNextionSource { Image = image, Degrees = degrees, Mirrored = mirrored };
    }

    // The screen splits along its long side: two portrait photos side by side on a
    // landscape screen, two landscape photos stacked on a portrait one.
    public static IReadOnlyList<SKRectI> Tiles(int width, int height, int count)
    {
        if (count < 2) return [new SKRectI(0, 0, width, height)];
        const int gap = 2;
        if (width > height)
        {
            var half = (width - gap) / 2;
            return [new SKRectI(0, 0, half, height), new SKRectI(width - half, 0, width, height)];
        }
        var mid = (height - gap) / 2;
        return [new SKRectI(0, 0, width, mid), new SKRectI(0, height - mid, width, height)];
    }

    public static byte[]? Compose(IReadOnlyList<OpenNextionSource> photos, IReadOnlyList<IReadOnlyList<string>> captions,
        int width, int height, OpenNextionFit fit)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var tiles = Tiles(width, height, photos.Count);
        for (var i = 0; i < tiles.Count; i++)
        {
            DrawTile(canvas, photos[i], tiles[i], fit);
            if (i < captions.Count) DrawCaption(canvas, captions[i], tiles[i]);
        }

        using var rendered = surface.Snapshot();
        using var jpeg = rendered.Encode(SKEncodedImageFormat.Jpeg, 85);
        return jpeg?.ToArray();
    }

    private static void DrawScaled(SKCanvas canvas, OpenNextionSource photo, SKRect target, bool cover)
    {
        var scaleX = target.Width / photo.Width;
        var scaleY = target.Height / photo.Height;
        var scale = cover ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
        canvas.Save();
        canvas.Translate(target.MidX, target.MidY);
        canvas.Scale(scale);
        canvas.RotateDegrees(photo.Degrees);
        if (photo.Mirrored) canvas.Scale(-1, 1);
        canvas.DrawImage(photo.Image, -photo.Image.Width / 2f, -photo.Image.Height / 2f, Sampling);
        canvas.Restore();
    }

    private static void DrawTile(SKCanvas canvas, OpenNextionSource photo, SKRectI tile, OpenNextionFit fit)
    {
        canvas.Save();
        canvas.ClipRect(tile);
        if (fit == OpenNextionFit.Blur) DrawBlurredBackground(canvas, photo, tile);
        DrawScaled(canvas, photo, tile, fit == OpenNextionFit.Cover);
        canvas.Restore();
    }

    // Draw a small cropped copy, blur it without a transform (so the radius is in
    // known pixels), then stretch it over the tile and darken it.
    private static void DrawBlurredBackground(SKCanvas canvas, OpenNextionSource photo, SKRectI tile)
    {
        // A third of the tile with a wide blur: smooth enough not to band or show
        // blocks after the panel's RGB565 conversion.
        var w = Math.Max(1, tile.Width / 3);
        var h = Math.Max(1, tile.Height / 3);
        using var small = SKSurface.Create(new SKImageInfo(w, h));
        using var blurred = SKSurface.Create(new SKImageInfo(w, h));
        if (small is null || blurred is null) return;
        DrawScaled(small.Canvas, photo, new SKRect(0, 0, w, h), true);
        using var smallImage = small.Snapshot();
        using (var blur = new SKPaint())
        {
            blur.ImageFilter = SKImageFilter.CreateBlur(8, 8, SKShaderTileMode.Clamp);
            blurred.Canvas.DrawImage(smallImage, 0, 0, blur);
        }
        using var background = blurred.Snapshot();
        canvas.DrawImage(background, SKRect.Create(w, h), tile, Sampling);
        using var shade = new SKPaint();
        shade.Color = new SKColor(0, 0, 0, 110);
        canvas.DrawRect(tile, shade);
    }

    // Photo details in the tile's top-left corner over a dark fade. The panel
    // keeps the bottom of the screen for its own clock and weather.
    private static void DrawCaption(SKCanvas canvas, IReadOnlyList<string> lines, SKRectI tile)
    {
        var text = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(3).ToList();
        if (text.Count == 0) return;
        var size = tile.Width < 300 ? 12f : 14f;
        using var font = new SKFont(Typeface.Value, size);
        var lineHeight = size * 1.3f;
        var pad = 8f;
        var bottom = tile.Top + pad + lineHeight * text.Count + pad * 2;
        using (var fade = new SKPaint())
        {
            fade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, tile.Top), new SKPoint(0, bottom),
                [new SKColor(0, 0, 0, 170), new SKColor(0, 0, 0, 0)], SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(tile.Left, tile.Top, tile.Right, bottom), fade);
        }
        using var shadow = new SKPaint();
        shadow.IsAntialias = true;
        shadow.Color = new SKColor(0, 0, 0, 160);
        using var ink = new SKPaint();
        ink.IsAntialias = true;
        ink.Color = SKColors.White;
        var maxWidth = tile.Width - pad * 2;
        var y = tile.Top + pad - font.Metrics.Ascent;
        foreach (var line in text)
        {
            var fitted = Ellipsize(line.Trim(), font, maxWidth);
            canvas.DrawText(fitted, tile.Left + pad + 1, y + 1, SKTextAlign.Left, font, shadow);
            canvas.DrawText(fitted, tile.Left + pad, y, SKTextAlign.Left, font, ink);
            y += lineHeight;
        }
    }

    internal static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth) return text;
        const string dots = "…";
        var end = text.Length;
        while (end > 0 && font.MeasureText(text[..end].TrimEnd() + dots) > maxWidth) end--;
        return end == 0 ? dots : text[..end].TrimEnd() + dots;
    }

    // The runtime image has no system fonts (SkiaSharp without fontconfig), so
    // the caption uses the web client's own Overpass font.
    private static SKTypeface LoadTypeface()
    {
        using var stream = typeof(OpenNextionPhoto).Assembly.GetManifestResourceStream("OpenNextion.Overpass.ttf");
        return (stream is null ? null : SKTypeface.FromStream(stream)) ?? SKTypeface.Default;
    }
}
