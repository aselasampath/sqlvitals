using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>An <see cref="IThemeService"/> that switches without touching any resources (#182).</summary>
internal sealed class FakeThemeService(AppTheme current = AppTheme.Light) : IThemeService
{
    public AppTheme Current { get; private set; } = current;

    public event EventHandler? Changed;

    public void Set(AppTheme theme)
    {
        if (theme == Current)
            return;
        Current = theme;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
