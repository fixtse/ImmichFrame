using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.WebApi.Helpers;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

[TestFixture]
public class OpenNextionHistoryTests
{
    private static AssetResponseDto Asset() => new() { Id = Guid.NewGuid() };

    [Test]
    public void StepsBackAndForwardThroughShownScreens()
    {
        var history = new OpenNextionHistory();
        IReadOnlyList<AssetResponseDto> a = [Asset()], b = [Asset()];
        history.Add(a);
        history.Add(b);
        Assert.Multiple(() =>
        {
            Assert.That(history.Step(back: true), Is.SameAs(a));
            Assert.That(history.Step(back: false), Is.SameAs(b));
            Assert.That(history.Step(back: false), Is.Null, "past the newest screen a new one is picked");
        });
    }

    [Test]
    public void AccountSettingChangeForgetsPhotosFromTheOldFilters()
    {
        var provider = new Mock<ISettingsProvider>();
        var history = new OpenNextionHistory(provider.Object);
        history.Add([Asset()]);
        history.Add([Asset()]);
        history.Carry(Asset());
        provider.Raise(p => p.SettingsChanged += null,
            new SettingsChangedEventArgs { NewSettings = Mock.Of<IServerSettings>(), AccountsChanged = true });
        Assert.Multiple(() =>
        {
            Assert.That(history.Step(back: true), Is.Null);
            Assert.That(history.TakeCarry(), Is.Null);
        });
    }
}
