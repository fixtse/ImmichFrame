using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.WebApi.Helpers;

// Screens the OpenNextion endpoint has shown, for going back, plus one photo held
// over from a pairing attempt. Everything here came from the configured asset
// pool. When the account settings (albums, people, filters) change it is
// cleared, so going back never shows a photo the new settings exclude.
// One screen: its photos and the JPEGs already rendered for it, by request
// options (size, fit, details), so stepping back or forward needs no new render.
public sealed class OpenNextionScreen(IReadOnlyList<AssetResponseDto> assets)
{
    public IReadOnlyList<AssetResponseDto> Assets { get; } = assets;
    private readonly Dictionary<string, byte[]> _rendered = new();

    public byte[]? Rendered(string key)
    {
        lock (_rendered) return _rendered.GetValueOrDefault(key);
    }

    public void Store(string key, byte[] jpeg)
    {
        // A panel uses one set of options; keep only the latest few.
        lock (_rendered)
        {
            if (_rendered.Count >= 2 && !_rendered.ContainsKey(key)) _rendered.Clear();
            _rendered[key] = jpeg;
        }
    }
}

public sealed class OpenNextionHistory : IDisposable
{
    public const int Size = 20;
    private readonly object _lock = new();
    private readonly List<OpenNextionScreen> _screens = new();
    private readonly ISettingsProvider? _settings;
    private int _cursor = -1;
    private AssetResponseDto? _carry;
    // The next new screen, picked and rendered in the background after a photo
    // is served, so the following request (or a swipe) returns at once.
    private (string Key, Task<OpenNextionScreen?> Task)? _prepared;

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
            _prepared = null;
        }
    }

    // True when stepping forward would pick a new screen (no newer history).
    public bool AtNewest
    {
        get { lock (_lock) return _cursor >= _screens.Count - 1; }
    }

    // Start preparing the next screen unless one is ready or pending for `key`.
    public void Prepare(string key, Func<Task<OpenNextionScreen?>> build)
    {
        lock (_lock)
        {
            if (_prepared is { } p && p.Key == key && !p.Task.IsFaulted) return;
            _prepared = (key, Task.Run(build));
        }
    }

    // The prepared screen for `key`, or null (none, other options or failed).
    public async Task<OpenNextionScreen?> TakePrepared(string key)
    {
        Task<OpenNextionScreen?> task;
        lock (_lock)
        {
            if (_prepared is not { } p || p.Key != key) return null;
            task = p.Task;
            _prepared = null;
        }
        try { return await task; }
        catch { return null; }
    }

    // A screen already shown: the previous one when going back, otherwise the
    // next one after an earlier back. Null when a new screen should be picked.
    public OpenNextionScreen? Step(bool back)
    {
        lock (_lock)
        {
            if (back && _cursor > 0) return _screens[--_cursor];
            if (back && _cursor == 0) return _screens[0];
            if (!back && _cursor >= 0 && _cursor < _screens.Count - 1) return _screens[++_cursor];
            return null;
        }
    }

    public void Add(OpenNextionScreen screen)
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
