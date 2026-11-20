using SkiaSharp;

namespace ImmichFrame.WebApi.Helpers;

internal static class OpenNextionPhoto
{
    public static async Task<byte[]?> Render(Stream stream, int width, int height, bool cover,
        CancellationToken cancellationToken)
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

        // Apply EXIF rotation and mirroring around the photo centre before fitting it to the screen.
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
        var sideways = degrees is 90 or 270;
        var scaleX = (float)width / (sideways ? photo.Height : photo.Width);
        var scaleY = (float)height / (sideways ? photo.Width : photo.Height);
        var scale = cover ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        canvas.Translate(width / 2f, height / 2f);
        canvas.Scale(scale);
        canvas.RotateDegrees(degrees);
        if (mirrored) canvas.Scale(-1, 1);
        using var source = SKImage.FromBitmap(photo);
        canvas.DrawImage(source, -photo.Width / 2f, -photo.Height / 2f,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

        using var rendered = surface.Snapshot();
        using var jpeg = rendered.Encode(SKEncodedImageFormat.Jpeg, 85);
        return jpeg?.ToArray();
    }
}
