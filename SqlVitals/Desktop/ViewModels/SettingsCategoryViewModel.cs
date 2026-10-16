using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A category of Settings (#131), as its view model (#190): one under <c>ViewModels/Settings</c> per
/// category's XAML. Settings' view model builds it the first time it is opened and keeps it while
/// Settings is open, asks it before leaving it, and tells it when it is opened again.
/// </summary>
public abstract class SettingsCategoryViewModel : ObservableObject
{
    private bool _active;

    /// <summary>What the category is about, under its title: from <see cref="SettingsNav"/>.</summary>
    public virtual string Subtitle => string.Empty;

    /// <summary>
    /// Whether the category can be left. With unsaved changes it asks to save, discard or stay;
    /// false when the user stays, or the save failed.
    /// </summary>
    public virtual Task<bool> TryLeaveAsync() => Task.FromResult(true);

    /// <summary>Opened again after another category: it picks up what changed meanwhile.</summary>
    public virtual void OnShownAgain() { }

    /// <summary>
    /// Its view is on screen: it follows the settings and the monitoring until
    /// <see cref="Deactivate"/>. The view calls both as it is loaded and unloaded; a second call
    /// changes nothing.
    /// </summary>
    public void Activate()
    {
        if (_active)
            return;
        _active = true;
        OnActivated();
    }

    /// <summary>Its view is off screen: it stops following what changes.</summary>
    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        OnDeactivated();
    }

    /// <summary>Subscribes to what it follows while shown.</summary>
    protected virtual void OnActivated() { }

    /// <summary>Unsubscribes what <see cref="OnActivated"/> subscribed.</summary>
    protected virtual void OnDeactivated() { }
}
