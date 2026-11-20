using ImmichFrame.Core.Api;
using ImmichFrame.WebApi.Controllers;
using ImmichFrame.WebApi.Helpers;
using NUnit.Framework;
using SkiaSharp;

namespace ImmichFrame.WebApi.Tests.Helpers;

[TestFixture]
public class OpenNextionPhotoTests
{
    [TestCase(320, 480)]
    [TestCase(480, 320)]
    [TestCase(240, 320)]
    [TestCase(320, 240)]
    public async Task RendersExactPanelSizeWithContainAndCover(int width, int height)
    {
        using var source = new SKBitmap(100, 100);
        source.Erase(SKColors.White);
        using var png = source.Encode(SKEncodedImageFormat.Png, 100);

        foreach (var cover in new[] { false, true })
        {
            using var input = png.AsStream();
            var jpeg = await OpenNextionPhoto.Render(input, width, height, cover, CancellationToken.None);
            Assert.That(jpeg, Is.Not.Null);
            using var decoded = SKBitmap.Decode(jpeg);
            Assert.Multiple(() =>
            {
                Assert.That((decoded.Width, decoded.Height), Is.EqualTo((width, height)));
                Assert.That(decoded.GetPixel(width / 2, height / 2).Red, Is.GreaterThan(240));
                var corner = decoded.GetPixel(0, 0).Red;
                if (cover)
                    Assert.That(corner, Is.GreaterThan(240));
                else
                    Assert.That(corner, Is.LessThan(16));
            });
        }
    }
}

[TestFixture]
public class OpenNextionCompositionTests
{
    private static OpenNextionSource Solid(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        return new OpenNextionSource { Image = SKImage.FromBitmap(bitmap) };
    }

    [Test]
    public void PairsPortraitPhotosSideBySideOnLandscapeScreen()
    {
        using var left = Solid(60, 100, SKColors.Red);
        using var right = Solid(60, 100, SKColors.Blue);
        var jpeg = OpenNextionPhoto.Compose([left, right], [[], []], 480, 320, OpenNextionFit.Cover);
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Multiple(() =>
        {
            Assert.That((decoded.Width, decoded.Height), Is.EqualTo((480, 320)));
            Assert.That(decoded.GetPixel(100, 160).Red, Is.GreaterThan(200));
            Assert.That(decoded.GetPixel(380, 160).Blue, Is.GreaterThan(200));
        });
    }

    [Test]
    public void StacksLandscapePhotosOnPortraitScreen()
    {
        var tiles = OpenNextionPhoto.Tiles(320, 480, 2);
        Assert.Multiple(() =>
        {
            Assert.That(tiles[0], Is.EqualTo(new SKRectI(0, 0, 320, 239)));
            Assert.That(tiles[1], Is.EqualTo(new SKRectI(0, 241, 320, 480)));
        });
    }

    [Test]
    public void BlurFillsTheBarsWithTheDarkenedPhoto()
    {
        using var wide = Solid(200, 100, SKColors.White);
        var jpeg = OpenNextionPhoto.Compose([wide], [[]], 320, 480, OpenNextionFit.Blur);
        using var decoded = SKBitmap.Decode(jpeg);
        var bar = decoded.GetPixel(160, 10);
        Assert.Multiple(() =>
        {
            Assert.That(bar.Red, Is.InRange(100, 220), "bars show a dimmed copy, not black");
            Assert.That(decoded.GetPixel(160, 240).Red, Is.GreaterThan(240));
        });
    }

    [Test]
    public void CaptionDrawsTextAtTheTop()
    {
        using var white = Solid(100, 100, SKColors.White);
        var plain = OpenNextionPhoto.Compose([white], [[]], 320, 480, OpenNextionFit.Cover);
        var captioned = OpenNextionPhoto.Compose([white], [["12.05.2024", "Summer holidays"]], 320, 480,
            OpenNextionFit.Cover);
        using var a = SKBitmap.Decode(plain);
        using var b = SKBitmap.Decode(captioned);
        Assert.Multiple(() =>
        {
            Assert.That(b.GetPixel(4, 2).Red, Is.LessThan(a.GetPixel(4, 2).Red - 40), "dark fade behind the text");
            Assert.That(b.GetPixel(160, 400).Red, Is.GreaterThan(240), "bottom left untouched");
        });
    }

    [Test]
    public void EllipsizesLongLines()
    {
        // The embedded caption font: test containers may have no system fonts.
        using var font = new SKFont(OpenNextionPhoto.CaptionTypeface, 14);
        var text = OpenNextionPhoto.Ellipsize(new string('W', 200), font, 100);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.EndWith("…"));
            Assert.That(font.MeasureText(text), Is.LessThanOrEqualTo(100));
        });
    }

    [TestCase(4000, 3000, null, false)]
    [TestCase(4000, 3000, "6", true)]
    [TestCase(3000, 4000, "1", true)]
    [TestCase(0, 0, null, null)]
    public void ReadsOrientationFromMetadata(int width, int height, string? orientation, bool? portrait)
    {
        var asset = new AssetResponseDto
        {
            ExifInfo = new ExifResponseDto { ExifImageWidth = width, ExifImageHeight = height, Orientation = orientation }
        };
        Assert.That(AssetController.OpenNextionPortrait(asset), Is.EqualTo(portrait));
    }
}
