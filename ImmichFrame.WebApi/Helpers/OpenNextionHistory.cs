using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.WebApi.Helpers;

// Screens the OpenNextion endpoint has shown, for going back, plus one photo held
// over from a pairing attempt. Everything here came from the configured asset
// pool. When the account settings (albums, people, filters) change it is
// cleared, so going back never shows a photo the new settings exclude.
public sealed class OpenNextionHistory : IDisposable
{
    public const int Size = 20;
    private readonly object _lock = new();
    private readonly List<IReadOnlyList<AssetResponseDto>> _screens = new();
    private readonly ISettingsProvider? _settings;
    private int _cursor = -1;
    private AssetResponseDto? _carry;

    public OpenNextionHistory(ISettingsProvider? settings = null)
    {
        _settings = settings;
        if (_settings != null) _settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        if (args.AccountsChanged) Clear();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _screens.Clear();
            _cursor = -1;
            _carry = null;
        }
    }

    // A screen already shown: the previous one when going back, otherwise the
    // next one after an earlier back. Null when a new screen should be picked.
    public IReadOnlyList<AssetResponseDto>? Step(bool back)
    {
        lock (_lock)
        {
            if (back && _cursor > 0) return _screens[--_cursor];
            if (back && _cursor == 0) return _screens[0];
            if (!back && _cursor >= 0 && _cursor < _screens.Count - 1) return _screens[++_cursor];
            return null;
        }
    }

    public void Add(IReadOnlyList<AssetResponseDto> screen)
    {
        lock (_lock)
        {
            _screens.Add(screen);
            if (_screens.Count > Size) _screens.RemoveAt(0);
            _cursor = _screens.Count - 1;
        }
    }

    public AssetResponseDto? TakeCarry()
    {
        lock (_lock)
        {
            var carry = _carry;
            _carry = null;
            return carry;
        }
    }

    public void Carry(AssetResponseDto asset)
    {
        lock (_lock) _carry ??= asset;
    }

    public void Dispose()
    {
        if (_settings != null) _settings.SettingsChanged -= OnSettingsChanged;
    }
}
