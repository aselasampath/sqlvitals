# Notifications and the Notification Area

SqlVitals can watch your servers in the background. It keeps an icon in the Windows **notification area** (beside the clock) and shows a **Windows notification** when an [alert](Alerts.md) starts, so you hear about a problem without keeping the window open.

> **Where:** the SqlVitals icon in the notification area · **Configure:** ⚙️ Settings → *Alerts → Windows notifications* and *Notification Area* · **Since:** 0.35.0

## Windows notifications

A notification appears when a monitored connection's alert:

- **starts**: e.g. **Critical: CPU on Sales-Prod**, *CPU 97% (threshold ≥ 90 %)*;
- or **turns critical**: **Now critical: Blocking on Sales-Prod**.

Alerts that start on the same sample share one notification, critical ones listed first: **2 alerts on Sales-Prod (1 critical)**. An alert getting worse or ending doesn't notify. The [Alerts page](Alerts.md#the-alerts-page) shows those.

**Click the notification** to bring SqlVitals to the front on the **🔔 Alerts** page, filtered to that connection. This works from the window being hidden, minimized or behind other windows.

Notifications follow the same rules as alerts: only for saved connections that are **monitored**, after the usual run of samples ([Settings → Alerts](Settings.md#alerts)). While monitoring is off there are no alerts, so there are no notifications; the icon's tooltip says *monitoring is off*.

### Mute a connection

Untick it in **⚙️ Settings → Alerts → Windows notifications**, or right-click the SqlVitals icon → **Notifications** → click the connection. A ticked connection notifies; an unticked one is muted.

- It's saved **straight away** and doesn't test the connection, so you can mute a server that's down.
- A muted connection's alerts are **still recorded** and listed on the Alerts page, and still count in the sidebar.
- New connections notify until you mute them.

### Windows' own settings still apply

SqlVitals uses the standard Windows notification system, so:

- **Do Not Disturb** / **Focus** hides them (they wait in the notification centre).
- **Windows Settings → System → Notifications → SqlVitals** can turn them off, or change whether they show banners or play a sound.

## Running in the notification area

By default, **minimizing** or **closing** the window hides it to the notification area. Monitoring, history, alerts and notifications carry on as if the window were open. The first time, a notification tells you SqlVitals is still running (or, if nothing is monitored, that you can start monitoring from the icon's menu).

| To… | Do this |
|---|---|
| Bring the window back | **Click** the SqlVitals icon, or start SqlVitals again from the Start menu or desktop |
| Open the Alerts page | Right-click the icon → **Alerts**. It shows how many are active, e.g. *Alerts (2 active)*. |
| See how many alerts are active | Hover the icon: *SqlVitals · 2 active alerts (1 critical)*, or *SqlVitals · monitoring is off, no alerts are raised* when no connection is monitored |
| Start or stop monitoring | Right-click the icon → **Start monitoring all** (the active connection and those with *Monitor in background*) or **Stop monitoring all** (every connection being monitored). Both last until SqlVitals exits. See [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring). |
| Mute or unmute a connection | Right-click the icon → **Notifications** |
| Quit SqlVitals | Right-click the icon → **Exit** |

The icon may be under the **^** arrow. Drag it next to the clock to keep it in sight.

**Exit** (or Windows signing you out or shutting down) quits for real, exactly as closing the window did before 0.35: active alerts end as *Monitoring stopped*, and a running [SP Trace](SP-Trace.md) session is dropped from the server.

### Prefer the old behaviour?

Untick **⚙️ Settings → Notification Area → Keep SqlVitals running in the notification area when the window is minimized or closed**. Minimizing then goes to the taskbar and closing the window exits. The icon and notifications stay while SqlVitals is open.

### One copy at a time

Starting SqlVitals while it's already running brings the running copy to the front instead of starting a second one, which would monitor the same servers twice, write the same history and notify twice. This is per Windows session, so two users signed in to the same PC each have their own.

## Good to know

- **No extra load on the server.** Notifications come from the samples SqlVitals already collects.
- **An open page keeps working while hidden.** A page that refreshes by itself (Live Metrics, Processes, SP Trace) carries on in the background. Switch to another page, or **Stop** SP Trace, before hiding the window if you don't want that.
- **Updating or uninstalling** SqlVitals needs it closed. Setup asks you to exit it from the notification area if it's still running.

---

### Under the hood

- **Icon and notifications:** `Desktop/Services/TrayIcon.cs` wraps WinForms' `NotifyIcon` (`UseWindowsForms` in `SqlVitals.Desktop.csproj`). Windows 10 and 11 show its balloon as an ordinary notification, attributed to the exe's description (`AssemblyTitle`: *SqlVitals*). It needs no AUMID, Start-menu registration or extra package. `BalloonTipClicked` raises `AlertsRequested(connectionId)`.
- **Menu:** *Start monitoring all* and *Stop monitoring all* raise `StartAllRequested` / `StopAllRequested`, which call `MonitoringManager.StartAll` / `StopAll` with the source `user: tray menu`. The tooltip comes from `AlertNotification.TrayToolTip(active, monitoredConnections)`.
- **Wording:** `Engine/Alerts/AlertNotification.cs` turns one sample's `AlertChange`s for a connection into a title and text (Windows allows 63 and 255 characters), or nothing when no alert started or escalated. Unit tested in `Engine.Tests/Alerts/AlertNotificationTests.cs`.
- **Wiring:** `MainWindow.OnAlertsChanged` handles `MonitoringManager.AlertsChanged`: it updates the sidebar count and the icon's tooltip, then notifies unless the connection is muted. Hiding is in `MainWindow.OnStateChanged` / `OnClosing`; `App.IsExiting` marks a real exit (tray **Exit**, `SessionEnding`). `App.xaml` uses `ShutdownMode="OnMainWindowClose"`.
- **Single instance:** `App.OnStartup` holds the named mutex `Local\SqlVitals.Desktop.SingleInstance`. A second copy signals the event `Local\SqlVitals.Desktop.Show` and exits; the first copy's listener thread calls `MainWindow.ShowFromTray()`.
- **Settings** in `settings.dat`: `NotificationsMuted` (connection ids; ids of deleted connections are dropped), `KeepRunningInTray` (default true), `TrayHintShown`. Added in [#35](https://github.com/aselasampath/sqlvitals/issues/35).
