namespace SqlVitals.Desktop.Services;

/// <summary>
/// How long a page's reads may run (#167): from when it is shown until it is left, or its connection
/// switches. The page factory binds the page's repository to it (<c>LifetimeRepository</c>), so
/// every query the page starts, from its view model or its code-behind, is cancelled on the server
/// when the page goes.
/// </summary>
public sealed class PageLifetime : IDisposable
{
    private CancellationTokenSource _current = new();

    /// <summary>Cancelled when the page is left; a page shown again gets a new one.</summary>
    public CancellationToken Token => _current.Token;

    /// <summary>The page is on screen (again): reads may run.</summary>
    public void Begin()
    {
        if (!_current.IsCancellationRequested)
            return;
        var ended = _current;
        _current = new CancellationTokenSource();
        ended.Dispose();
    }

    /// <summary>The page was left: whatever it is reading stops.</summary>
    public void End() => _current.Cancel();

    /// <summary>The loading bar's Stop (#117): whatever the page is reading stops, and it may read again.</summary>
    public void Restart()
    {
        End();
        Begin();
    }

    public void Dispose() => _current.Dispose();
}
