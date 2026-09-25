using System.Windows;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// SqlVitals' icon in the Windows notification area (#35). It brings the window back, raises a
/// Windows notification when an alert starts, and its menu mutes connections or exits the app.
/// A notification-area balloon is shown by Windows 10 and 11 as an ordinary notification, so no
/// app registration or extra package is needed. Created and used on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ConnectionSettingsService _settings;
    private readonly Forms.NotifyIcon          _icon;
    private readonly Forms.ContextMenuStrip    _menu;
    private readonly Forms.ToolStripMenuItem   _alertsItem;
    private readonly Forms.ToolStripMenuItem   _notificationsItem;

    // What clicking the last notification opens: the Alerts page for its connection, or, for a
    // notification that isn't about an alert, just the window.
    private Guid? _notifiedConnection;

    /// <summary>The icon was clicked, or Open was chosen: show the window.</summary>
    public event Action? OpenRequested;

    /// <summary>Show the Alerts page, filtered to a connection when not null.</summary>
    public event Action<Guid?>? AlertsRequested;

    /// <summary>Exit was chosen: quit SqlVitals for real.</summary>
    public event Action? ExitRequested;

    public TrayIcon(ConnectionSettingsService settings)
    {
        _settings = settings;

        var open = new Forms.ToolStripMenuItem("Open SqlVitals");
        open.Font   = new Drawing.Font(open.Font, Drawing.FontStyle.Bold);   // the default action, as Windows shows it
        open.Click += (_, _) => OpenRequested?.Invoke();

        _alertsItem = new Forms.ToolStripMenuItem("Alerts");
        _alertsItem.Click += (_, _) => AlertsRequested?.Invoke(null);

        _notificationsItem = new Forms.ToolStripMenuItem("Notifications")
        {
            ToolTipText = "Ticked connections raise a Windows notification when an alert starts",
        };

        var exit = new Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new Forms.ContextMenuStrip { ShowItemToolTips = true };
        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            open, _alertsItem, new Forms.ToolStripSeparator(), _notificationsItem, new Forms.ToolStripSeparator(), exit,
        });
        // Rebuilt each time, so it lists the connections and choices saved right now.
        _menu.Opening += (_, _) => FillNotificationsMenu();

        _icon = new Forms.NotifyIcon
        {
            Icon             = LoadIcon(),
            Text             = AlertNotification.TrayToolTip([]),
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_notifiedConnection is { } id)
                AlertsRequested?.Invoke(id);
            else
                OpenRequested?.Invoke();
        };
        _icon.Visible = true;
    }

    /// <summary>Raises the Windows notification for an alert.</summary>
    public void Notify(AlertNotification notification)
    {
        _notifiedConnection = notification.ConnectionId;
        _icon.ShowBalloonTip(
            (int)TimeSpan.FromSeconds(10).TotalMilliseconds, notification.Title, notification.Text,
            notification.Severity == HealthLevel.Critical ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
    }

    /// <summary>Raises a Windows notification that isn't about an alert; clicking it shows the window.</summary>
    public void Inform(string title, string text)
    {
        _notifiedConnection = null;
        _icon.ShowBalloonTip((int)TimeSpan.FromSeconds(10).TotalMilliseconds, title, text, Forms.ToolTipIcon.Info);
    }

    /// <summary>Puts the number of active alerts in the icon's tooltip and menu.</summary>
    public void ShowActiveAlerts(IReadOnlyCollection<Alert> active)
    {
        _icon.Text       = AlertNotification.TrayToolTip(active);
        _alertsItem.Text = active.Count == 0 ? "Alerts" : $"Alerts ({active.Count} active)";
    }

    private void FillNotificationsMenu()
    {
        _notificationsItem.DropDownItems.Clear();

        var store = _settings.Load();
        if (store.Connections.Count == 0)
        {
            _notificationsItem.DropDownItems.Add(new Forms.ToolStripMenuItem("No saved connections") { Enabled = false });
            return;
        }

        foreach (var conn in store.Connections.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var id       = conn.Id;
            var notifies = store.NotifiesFor(id);
            var item     = new Forms.ToolStripMenuItem(conn.DisplayName)
            {
                Checked     = notifies,
                ToolTipText = notifies ? "Click to mute this connection's notifications" : "Muted: click to be notified again",
            };
            item.Click += (_, _) =>
            {
                try
                {
                    _settings.SetNotificationsMuted(id, muted: notifies);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Tray: could not save the notification setting", ex);
                }
            };
            _notificationsItem.DropDownItems.Add(item);
        }
    }

    // The branded icon at the size the notification area draws; the exe's own as a fallback.
    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/SqlVitals.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray: could not load the icon", ex);
        }

        return (Environment.ProcessPath is { } exe ? Drawing.Icon.ExtractAssociatedIcon(exe) : null)
               ?? Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        // Otherwise the icon lingers in the notification area until the mouse passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
