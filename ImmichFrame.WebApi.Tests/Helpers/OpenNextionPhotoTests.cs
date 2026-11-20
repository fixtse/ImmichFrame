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
