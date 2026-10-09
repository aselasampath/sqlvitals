# Chat Notifications

A Windows notification only reaches the person at the PC that runs SqlVitals. **Chat channels** post alerts to a **Slack** or **Microsoft Teams** channel when they start, turn critical and end, so the whole team sees a problem when it starts and knows when it's over, even when nobody is looking at that PC.

> **Where:** ⚙️ Settings → *Alerts & chat* → *Chat channels* · **Since:** 0.52.0 ([epic #108](https://github.com/aselasampath/sqlvitals/issues/108)) · *Stopped watching* and *watching again* since 0.70.0, the [heartbeat](#heartbeat) since 0.71.0; how much was [monitored](Your-Servers.md#how-much-was-monitored) shows on Home and the Overview since 0.72.0 ([#170](https://github.com/aselasampath/sqlvitals/issues/170))

Both services take a **webhook**: a URL you create for one channel. Anything that sends it a message appears in that channel. SqlVitals needs no app, bot, API key or account of its own.

## What you need

| Service | Plan | Notes |
|---|---|---|
| **Slack** | Any plan, the **free plan** included (it allows up to 10 apps) | A workspace can require an admin to approve new apps. Each webhook posts to one channel. |
| **Microsoft Teams** | A **Microsoft 365 work or school account** (any business plan) | The free personal Teams can't do it. No Power Automate Premium licence and no Azure subscription are needed. |

Messages are sent **only while SqlVitals is running on this PC**, over HTTPS from this PC to Slack or Microsoft. Nothing is installed or changed on your SQL Servers.

> ⚠️ **Alerts stop when this PC sleeps, shuts down, or SqlVitals is closed.** Nothing is watched while it isn't running. Each channel is told when SqlVitals [stops watching and starts again](#stopped-watching-and-watching-again), but not if the PC loses power or SqlVitals is ended by force. A quiet channel doesn't mean the servers are fine. The *Chat channels* card in Settings says this too. To be told even then, turn on the channel's [heartbeat](#heartbeat) and set up an alert for when one doesn't arrive.

## Slack

1. Go to **[api.slack.com/apps](https://api.slack.com/apps)** and sign in to your workspace.
2. **Create New App** → **From scratch**. Name it, e.g. *SqlVitals*, and pick the workspace.
3. Under **Features**, open **Incoming Webhooks** and turn **Activate Incoming Webhooks** on.
4. Click **Add New Webhook**, pick the channel alerts should go to, and click **Allow**. (If your workspace needs approval, an admin gets the request first.)
5. Copy the **Webhook URL**. It starts with `https://hooks.slack.com/services/`.

Make one webhook per channel. A link from Slack's *Workflow Builder* (`https://hooks.slack.com/triggers/…`) is a different kind of webhook that doesn't take SqlVitals' messages, so SqlVitals refuses it.

## Microsoft Teams

Teams channels take messages through a **workflow** (Power Automate, built into Teams).

1. In Teams, go to the channel, click **•••** (More options) next to its name, then **Workflows**.
2. Pick the template **Post to a channel when a webhook request is received**.
3. Name it, e.g. *SqlVitals alerts*, check the account it connects with, and click **Next**.
4. Check the **Team** and **Channel**, then click **Add workflow**.
5. Copy the URL Teams shows. It's long, and usually on a host ending `logic.azure.com` or `api.powerplatform.com`.

Good to know:

- The workflow **runs as the person who created it**. If that account is disabled or loses its licence, the workflow stops, and posting stops with it. For a team channel, create it with an account that will stay, such as a shared service account.
- The workflow must allow **Anyone** to trigger it. That's what this template does by default; your organisation's policies can change it.
- Teams only accepts **Adaptive Cards**, not plain text. SqlVitals sends a card (version 1.4).
- Teams answers a message with *202 Accepted*: the workflow took it, but that doesn't prove the card was posted. The test result says so. If a test is accepted but nothing appears, open the workflow's run history (Teams → **Workflows**).

**Why not the Office 365 "Incoming Webhook" connector?** Microsoft is retiring Office 365 connectors in Teams. Their URLs (on `webhook.office.com`) are refused, with a link to the steps above.

## Add the channel in SqlVitals

In **⚙️ Settings → Alerts & chat → Chat channels**, click **+ Add channel**:

| Field | What to enter |
|---|---|
| **Name** | What you call it, e.g. *DBA team (Teams)*. Shown in Settings, the activity log and the app log. |
| **Webhook URL** | The URL you copied. Once saved it's shown masked, e.g. `hooks.slack.com/…/x7Qa`; click **Show** to see or change it. |
| **Service** | Slack or Microsoft Teams. Picked from the URL when SqlVitals recognises it; you can change it. |
| **Connections** | **All monitored connections** (the default): every connection SqlVitals is monitoring at the time, including ones you add later. Or **Only these**, ticked from your saved connections. A connection you remove is taken off the list. |
| **Post** | **Warnings and critical** (the default), or **Critical only**. |
| **Also post when an alert ends** | On by default: *Back to normal* for the alerts this channel was told about. Off, only starts and escalations are posted. *Stopped watching* and *watching again* are posted either way. |
| **Heartbeat** | **Off** (the default), **Every hour** … **Every 12 hours**, or **Daily at** a time: a *💓 SqlVitals heartbeat* message saying it's still watching. See [Heartbeat](#heartbeat). |
| **Heartbeat ping URL** | Optional: a dead man's switch check that each heartbeat also calls. Stored encrypted and shown masked once saved, like the webhook. See [Heartbeat](#heartbeat). |
| **On** | Turn off to pause the channel without removing it. What it had waiting is dropped. A test message can still be sent. |

Click **💾 Save channel**. The URL is checked first:

| Check | If it fails |
|---|---|
| It's a whole URL starting `https://` | Refused |
| Not an Office 365 connector (`*.webhook.office.com`) | Refused, with a link to the Teams steps |
| A Slack channel's URL is on `hooks.slack.com/services/` | Refused |
| A Teams channel's URL is on a host SqlVitals knows | **Allowed with a warning**: Microsoft has changed the host before, so send a test message to check it |
| No other channel has the same URL | Refused, naming that channel. Letter case in the host, the `:443` port and a trailing `/` don't make a URL different. |

Select a channel to edit it, or click **🗑 Remove** to remove it. The webhook itself stays in Slack or Teams until you delete it there. Every change applies from the next alert, with no restart.

## What is posted, and when

A channel gets a message when an alert starts or turns critical, the same moments as a [Windows notification](Notifications-and-Tray.md#windows-notifications), and when it ends:

| When | Message | Posted to |
|---|---|---|
| An alert **starts** | 🔴 *Critical: CPU on Sales-Prod* or 🟠 *Warning: Blocking on Sales-Prod* | Channels that take the connection; warnings only to *Warnings and critical* channels |
| A warning **turns critical** | 🔴 *Now critical: CPU on Sales-Prod* | Channels that were told about the warning |
| A warning a channel skipped **turns critical** | 🔴 *Critical: CPU on Sales-Prod*, as a new alert | *Critical only* channels, and a channel that was off (or not yet added) when the warning started |
| An alert **gets worse** | Nothing | |
| An alert is **back to normal** | ✅ *Back to normal: CPU on Sales-Prod* ([below](#back-to-normal)) | Channels that were told about the alert and have **Also post when an alert ends** on |
| SqlVitals **stops watching** a server | ⏹ *SqlVitals stopped watching Sales-Prod*, with why and the alerts still active ([below](#stopped-watching-and-watching-again)) | Channels that take the connection, or were told about one of its alerts, whatever **Also post when an alert ends** says |
| SqlVitals **starts watching** it again | ▶ *SqlVitals is watching Sales-Prod again*, with how long it wasn't watched | Channels that take the connection |

**Ends follow starts.** A channel only hears the end of an alert it was told about. It isn't told the end of an alert it filtered out (another connection, or a warning on a *Critical only* channel), and it is told the end of one it was told about even if its **Connections** or **Post** changed since. A warning that turned critical and was posted to a *Critical only* channel posts its end.

Alerts of one connection that start on the same sample share one message, critical first: *🔴 3 alerts on Sales-Prod (1 critical)*, with a line each. After 10 lines it says *…and N more*.

Each message has:

- the severity: 🔴 Critical or 🟠 Warning;
- the connection's name and the indicator;
- the figure and threshold as the [Alerts](Alerts.md) page shows them, e.g. *97 % (threshold ≥ 90 %)*, and what the health dot said when it adds something, e.g. *Log 93% full (Sales)* or *Blocking 3.5 min (4 blocked)*;
- when it started, shown in **each reader's own time zone** (Slack's date token; the Adaptive Card DATE and TIME functions in Teams);
- **Sent from SqlVitals on &lt;PC name&gt;**, so a team with several copies of SqlVitals watching the same server can tell them apart.

**Slack** gets Block Kit, with a plain text line that Slack's own notifications show:

> **🔴 Critical: CPU on Sales-Prod**
> **Figure** 97 % (threshold ≥ 90 %) · **Started** Today 10:42
> *Sent from SqlVitals on DBA-PC01*

**Teams** gets an Adaptive Card with the title in red (critical) or amber (warning), the figure in a fact set, and the start time under it:

> **🔴 Critical: CPU on Sales-Prod**
> Figure: 97 % (threshold ≥ 90 %)
> **Started** 30/09/2026 10:42
> *Sent from SqlVitals on DBA-PC01*

### Back to normal

When an alert ends because its figure has been back within its thresholds for the [usual run of samples](Alerts.md#how-an-alert-starts-and-ends), the channel gets how long it lasted, its worst figure, and when it started and ended, in each reader's own time zone. Teams shows the title in green.

> **✅ Back to normal: CPU on Sales-Prod**
> **Worst** 97 % (threshold ≥ 90 %) · **Lasted** 12 min · **Started** Today 10:42 · **Ended** Today 10:54
> *Sent from SqlVitals on DBA-PC01*

Alerts of one connection that end on the same sample share one message: *✅ 2 alerts back to normal on Sales-Prod*, with a line each (*CPU worst 97 % (threshold ≥ 90 %), lasted 12 min*).

An alert that keeps ending and starting again, a figure hovering around its threshold, posts each time. The run of samples an alert needs to start and to end already stops most of that; if a channel gets noisy, raise **Settings → Alerts & chat → Raise an alert after**, or turn off **Also post when an alert ends** for that channel.

### Stopped watching and watching again

A quiet channel should mean the servers are fine, not that nobody is looking. So whenever SqlVitals **stops watching** servers, each channel that takes them gets one message saying so, and why:

> **⏹ SqlVitals stopped watching Sales-Prod**
> SqlVitals on DBA-PC01 stopped watching Sales-Prod because the PC is going to sleep. No alerts will be posted for it until SqlVitals is watching again. Sales-Prod had 2 alerts still active: CPU, Blocking.
> **Stopped** Today 18:02

| What happened | The message says *because…* |
|---|---|
| You pressed **Stop** (a server's Overview or Live charts, the sidebar's server header, a tile's **⋯** menu, or **Stop monitoring all** in the tray menu) | monitoring was stopped in SqlVitals |
| You turned off **Start monitoring automatically** | Start monitoring automatically was turned off |
| You opened another server, and **Monitor in background** is off for this one | another server was opened, and Monitor in background is off |
| Its saved password or Microsoft sign-in is missing now | a saved password or sign-in is missing |
| You removed the connection | the connection was removed from SqlVitals |
| You chose **Exit**, or closed the window with *Keep SqlVitals running in the notification area* off | the app was closed |
| Windows is shutting down or restarting | Windows is shutting down |
| You signed out of Windows | the Windows user signed out |
| The PC is going to sleep or hibernating | the PC is going to sleep |

- It names **the alerts still active** that the channel was told about. Their *Back to normal* isn't posted, because nobody is watching any more: an alert's end needs someone to see it.
- Servers that stop at once share one message: *⏹ SqlVitals stopped watching 3 servers*, with a line each when they stopped for different reasons or had alerts active.
- The **Stopped** time is shown in each reader's own time zone, so a message that only gets out after the PC wakes still says when it stopped.
- It's sent whether or not **Also post when an alert ends** is on, and never [combined](#a-channel-that-falls-behind) into a summary.
- **Editing a connection** (its server, sign-in or timeout) restarts its monitoring without a message: it was never unwatched. Its alerts end, and are posted again as new ones after the usual run of samples if the problem is still there.
- **A removed connection** is named as it was.

When SqlVitals **starts watching** again (when it starts, you press **Start**, or the PC wakes), each channel that takes the server is told how long it wasn't watched:

> **▶ SqlVitals is watching Sales-Prod again**
> SqlVitals on DBA-PC01 is watching Sales-Prod again, after 7 h 05 min without monitoring.
> **Not watched for** 7 h 05 min · **Last watched** Today 01:12

The gap runs from its last sample in the [history](Monitoring-History-Database.md), or from when it stopped earlier in the same run, so it's right after a crash too. A server SqlVitals has never watched (or whose history was cleared) says *SqlVitals is watching Sales-Prod* instead. Several servers starting at once share one message, with a line each: *Sales-Prod not watched for 7 h 05 min · since 01:12*.

**On Exit**, Windows shutting down or signing out, every server stops at once, so each channel gets one *stopped watching* message for all of them. SqlVitals then gives the channels **up to 5 seconds** to send what's waiting, while it saves its history. A message waiting to retry gets one try straight away, and nothing is retried. What's still waiting after that is dropped and written to the [app log](Settings.md#diagnostics). Windows may cut a shutdown short, or the network may already be down. Closing or minimizing the window to the [notification area](Notifications-and-Tray.md#running-in-the-notification-area) isn't an exit: monitoring and posting carry on.

**When the PC goes to sleep**, SqlVitals keeps running but collects nothing. It holds the sleep back for **up to 2 seconds** to send *stopped watching*; what isn't sent by then goes when the PC wakes, followed by *watching again*. On some laptops (Modern Standby) Windows doesn't say it's going to sleep, and then neither message is posted.

**If SqlVitals is killed** (Task Manager, a crash) **or the PC loses power**, there's no *stopped watching*: nothing is left running to send it. The next start still posts *watching again* with the real gap. The alerts that were active are ended then, as the Alerts page shows, but that posts nothing: it would be late and could mislead.

### Heartbeat

*Stopped watching* can't be sent when the PC loses power or SqlVitals is killed, and not every sleep is announced. The **heartbeat** covers that: a short message on a schedule you pick, so that **a missing heartbeat** is the alert.

> **💓 SqlVitals heartbeat: watching 2 servers**
> SqlVitals on DBA-PC01 is running and watching HR-DB and Sales-Prod.
> **Next heartbeat** Tomorrow 09:00 · **Schedule** Daily at 09:00

- Choose it per channel, in the channel's form: **Off** (the default), **Every hour**, **Every 2, 3, 4, 6, 8 or 12 hours**, or **Daily at** a time (on the hour or half past).
- Times are **this PC's clock**, and hours count from midnight: *every 6 hours* is 00:00, 06:00, 12:00 and 18:00, whenever SqlVitals started, so a check knows when to look. A time that doesn't exist when clocks go forward is sent an hour later; one that happens twice when they go back is sent once.
- It lists the servers being watched **that this channel takes**. Watching none, it says so as a warning (*watching no servers*): SqlVitals is running, but nothing will be alerted.
- A heartbeat missed while the PC slept is sent **once**, late, when it wakes; then the schedule carries on.
- It's never [combined](#a-channel-that-falls-behind) into a summary, and the card shows a *Heartbeat daily 09:00* tag (*+ ping* with a ping URL).

#### Get an alert when a heartbeat is missing

In the channel's form, **How to get an alert when a heartbeat is missing** shows the steps for that channel's service and schedule, and **Copy steps** copies them.

**Any service: a dead man's switch (the only way for Slack).** Slack can't notice that a message didn't arrive, so a monitoring service does it:

1. Create a check at [Healthchecks.io](https://healthchecks.io) (it has a free plan), [Cronitor](https://cronitor.io) or [Better Stack](https://betterstack.com/uptime).
2. Expect a ping at the heartbeat's interval (every day, every 6 hours…), with a grace time of 30 minutes.
3. Connect the service to Slack or Teams, so it posts to the channel when the check goes down.
4. Paste the check's **ping URL** into **Heartbeat ping URL**, and save the channel.

SqlVitals calls the ping URL (an HTTPS `GET`) with each heartbeat **while it's watching at least one server the channel takes**, so the check also goes down when SqlVitals runs but watches nothing. A ping is tried once; whether it was answered is written to the [app log](Settings.md#diagnostics), never the URL. The URL must start with `https://` and can't be the channel's webhook.

**Teams only: a scheduled Power Automate flow.** In Teams (**Workflows**) or at make.powerautomate.com, create a scheduled cloud flow:

1. **Recurrence**: at the heartbeat's interval, starting 30 minutes after a heartbeat (09:30 for *Daily at 09:00*; 00:30 for *every 6 hours*), in the PC's time zone.
2. **Microsoft Teams → Get messages**: the team and channel.
3. **Data operation → Filter array**: from the value of *Get messages*, in advanced mode (6 is the interval in hours; 24 for daily):
   ```
   @and(contains(string(item()?['attachments']), 'SqlVitals heartbeat'), greater(ticks(item()?['createdDateTime']), ticks(addHours(utcNow(), -6))))
   ```
   SqlVitals' messages are cards, so their text is in the attachments, not the message body.
4. **Condition**: `length(body('Filter_array'))` is equal to 0.
5. **If yes → Microsoft Teams → Post message in a chat or channel**: *⚠ No heartbeat from SqlVitals on DBA-PC01. Check that it's running and the PC is awake.*

*Get messages* is a preview action. If the flow starts alerting while heartbeats arrive, look at its output in the flow's run history.

### Separate from Windows notifications

Muting a connection's Windows notifications (on its **Monitoring & alerts** tab in Settings, or the tray menu) doesn't stop its chat messages, and a channel's **Connections** don't change who gets Windows notifications.

### What leaves your PC

Only what the message shows: connection names, indicator names, figures and thresholds, database names in a log-space alert, times, and the PC's name. Never a server's connection string, a login, a password, query text or the webhook URL. If a connection has no name of its own, its name is its server and database, so name your connections if their server names are private. The messages go to Slack or Microsoft and stay in the channel's history.

### Limits

- **Only while SqlVitals runs on this PC**, and only for the connections it's monitoring at the time (see [Starting and stopping monitoring](Health-Dot-and-Background-Monitoring.md#starting-and-stopping-monitoring)). Keep it running in the [notification area](Notifications-and-Tray.md#running-in-the-notification-area). Each channel hears when it [stops and starts watching](#stopped-watching-and-watching-again), but not when the PC loses power or SqlVitals is killed; a [heartbeat](#heartbeat) with an alert for a missing one covers that.
- **One PC's view.** Two copies of SqlVitals watching the same server both post; the *Sent from* line tells them apart.
- **Not remembered across restarts.** After SqlVitals restarts, an alert that's still happening is raised again and posted again as a new alert. That's expected. An alert already active when a channel is added or turned on isn't posted until it turns critical, and then its end is posted too.
- Each channel sends its messages in order, one at a time and at most one a second, without holding up monitoring, the window or other channels.

## When messages don't get through

### Retries

Slack or Teams can be busy for a moment, or the network can drop. SqlVitals tries a message again, depending on the answer:

| Answer | Tried again? |
|---|---|
| **Too many messages (429)** | Yes, after the time the service asks for (*Retry-After*), or 30 seconds if it doesn't say, up to 5 minutes. After 5 of these in a row the message is given up, so a channel that's always refused still ends up *Failing*. |
| **Server error (5xx)**, **can't reach the service**, **timed out** | Up to **3 times**, after 2, 10 and 30 seconds |
| **Webhook not found (404 / 410)**, **rejected** (400, 401, 403…), a **proxy that asks for a sign-in** (407), an **unexpected answer**, a redirect | No: asking again gets the same answer. Fix what it says. |

- A message waiting to be retried keeps its place: the messages after it wait behind it, so they arrive in order. It never holds up another channel, monitoring or the window.
- In the **minute after the PC wakes from sleep**, while Wi-Fi or the VPN reconnects, *can't reach the service* is tried every 10 seconds without using up a retry. So opening a laptop with messages waiting doesn't raise a false warning.
- Each try is written to the [app log](Settings.md#diagnostics) by the channel's name, e.g. `Chat "DBA team" (Slack): alert on Sales-Prod server error (503); retry 1 of 3 in 2 s`, and a message that got through on a retry says so. The webhook URL is never in the log.

### A channel that falls behind

Messages go at most **one a second** per channel, Slack's limit. If more than **20** are waiting (a retry holding the line, or many alerts at once), the rest are combined into one message, sent after the 20:

> **📋 14 more messages combined**
> 14 more messages were combined while the channel was busy. See the Alerts page in SqlVitals for the details.
> 🔴 **Sales-Prod** new or critical: CPU and Log space used
> 🟠 **HR-DB** new or critical: Blocking; back to normal: CPU

Once it's sent, messages go one by one again. A **stopped watching**, **watching again** or **heartbeat** message is never combined: it's sent on its own after the summary, because "no alerts will be posted" mustn't become a count.

### ⚠ Failing

When a message is given up (after its retries, or straight away for a 404), the channel's card in Settings is labelled **FAILING** and says why:

> ⚠ **Failing since 10:42: webhook not found (404)**

and one Windows notification says so, even while SqlVitals is hidden in the notification area:

> **Chat channel failing**
> Alerts aren't reaching DBA team (Microsoft Teams): webhook not found. Check Settings → Alerts & chat.

- *Failing since* is when the first message was given up; the reason is the latest one.
- The notification isn't repeated while the channel keeps failing. It comes again only if the channel posts successfully and then fails again.
- New alerts are still tried, so a channel that comes back **recovers by itself**: the next message that gets through sets it back to *Last posted*. A **test message** that gets through does the same. One that fails doesn't mark the channel failing: its result is shown by the button.
- It isn't about a connection, so muting a connection's Windows notifications doesn't hide it.
- Turning a failing channel **off** clears it.

What each reason means, and what to do, is in the [Send test message](#send-test-message) table below: send a test message once you've fixed it.

### The same message twice

A channel can occasionally get the same message twice. That's expected:

- **A retry after a timeout or server error.** Slack or Microsoft may have posted the message before the error, and a webhook can't tell SqlVitals it already has it. The log says the message was retried.
- **An alert still happening after a restart** is posted again as a new alert (see [Limits](#limits)).
- **Several copies of SqlVitals** watching the same server, each posting to the same channel. Tell them apart by *Sent from SqlVitals on &lt;PC&gt;*.

## Send test message

Select a channel and click **📨 Send test message**. SqlVitals posts *"Test message from SqlVitals on &lt;PC name&gt;. Alerts for &lt;connections&gt; will be posted here."* (e.g. *all monitored connections*, or *HR-DB and Sales-Prod*) once, without retrying, and shows the result:

| Result | What it means | What to do |
|---|---|---|
| **Sent in 312 ms** | Slack posted it | Nothing |
| **Accepted by the workflow in 480 ms. Check the channel** | Teams' workflow took it | Look in the channel. If it isn't there, check the workflow's run history. |
| **The webhook doesn't exist or was removed (404 / 410)** | The URL is wrong, the webhook or workflow was deleted, or the Slack channel was archived | Create a new webhook and paste its URL |
| **Rejected by Slack (403): invalid_token** (or another code) | The service refused it and said why: `invalid_token`, `action_prohibited` (an admin restricted the app), `invalid_payload`; for Teams a 401 or 403 usually means the workflow doesn't allow *Anyone* or a policy blocks it | Fix what it names, or ask your Slack or Microsoft 365 admin |
| **Too many messages (429)** | Slack takes about one message per second per webhook | Try again after the time shown |
| **Server error (5xx)** | Slack or Microsoft had a problem | Try again in a minute |
| **Unexpected answer** | Something other than Slack or Teams answered: a proxy's block page, or a Wi-Fi sign-in page | Sign in to the network, or ask IT to allow the host |
| **Can't reach Slack / Microsoft Teams** | DNS, a firewall, the proxy (for example one that asks for a sign-in) or HTTPS inspection stopped the request | Allow outbound HTTPS from this PC to `hooks.slack.com`, or to your workflow's host |
| **Timed out** | No answer within 10 seconds | Check the network, then try again |

Each channel's row shows **Never used**, **Last posted 10:42** or **⚠ Failing since 10:42: …** ([see above](#-failing)). A test message that gets through clears *Failing*; one that fails leaves the status as it was. The status is kept while SqlVitals runs, so it starts again at *Never used* after a restart.

Requests use the **Windows proxy settings** (including an automatic configuration script), signed in as your Windows user, so corporate networks work. A redirect isn't followed, so a message can never end up on a host you didn't choose.

## The webhook URL is a secret

Anyone who has a webhook URL can post to the channel, so SqlVitals treats it like a password:

- It's saved in `settings.dat`, **encrypted with DPAPI** for your Windows user, like your connections.
- It's never written to the [app log](Settings.md#diagnostics), to exception messages or to the [activity log](Settings.md#activity-log) (which only says the URL *changed*), and it's in no export.
- Settings shows it masked until you click **Show**.
- A `settings.dat` copied to another PC or Windows user, or one whose Windows password was reset by an administrator, can't be decrypted there. Its chat channels are then dropped: SqlVitals says so with a Windows notification when it starts and a warning in Settings, and the rest of the settings still load. Add the channels again.

---

### Under the hood

- **Pure logic** (`SqlVitals/Engine/ChatNotifications/`, unit tested in `Engine.Tests/ChatNotifications/`): `ChatChannel`; `WebhookUrl` checks, compares and masks URLs; `ChatMessageBuilder` builds a `ChatMessage`, which `ChatFormatter` writes as Slack Block Kit (with a `text` fallback, and `&`, `<`, `>` escaped so a name can't mention `@channel`) or as an Adaptive Card 1.4 in the `{"type":"message","attachments":[…]}` envelope Teams workflows take; `WebhookResult` maps an answer or an exception to an outcome, from the status code and the exception's type only, never its message.
- **Sending:** `IWebhookSender` is the only way out, so tests use a fake. `Desktop/Services/HttpWebhookSender.cs` is one `HttpClient` over a `SocketsHttpHandler` with the system proxy and default credentials, no redirects, and a 10-second timeout per request. It reads at most 1 KB of the answer.
- **What to post:** `ChannelRouter`, one per channel, turns a connection's `AlertChange`s from one sample into at most one message of each kind, and remembers in memory the alert ids it told the channel about, with the connection's name then (forgotten when the alert ends or the channel is turned off). An `Ended` change of a told alert is `ChatMessageBuilder.Recovered` (when `ChatChannel.PostEnds`) or, with `AlertEndReason.MonitoringStopped`, kept per connection for `TakeStops`. `ChatMessageBuilder` words them with `AlertText` (`Figure`, `Context`, `Duration`), as the Alerts page and `AlertNotification` do. Unit tested in `ChannelRouterTests`, `AlertMessageTests`, `EndMessageTests` and `WatchMessageTests` (expected Slack and Teams JSON).
- **Stopped watching and watching again (#170):** `MonitoringManager.Sync` raises `WatchingChanged` with a `WatchChange` (Engine/Monitoring/Watching.cs): the connections it started, each with `LastWatchedUtc` (when it stopped earlier in the run, else `HistoryReader.ReadLastSampleTime`, the last `health_scores` row), the ones it stopped with a `WatchStop` reason, and the ones restarted with new details. `ChatNotificationService.OnWatchingChanged` keeps the set being watched, and per channel queues `ChatMessageBuilder.StoppedWatching` (with the router's `TakeStops`) and `WatchingAgain`; restarts drop their stops. `MainWindow.OnPowerModeChanged` calls `OnGoingToSleep` on the UI thread and waits up to 2 s for the queues (`WhenIdleAsync`), and `OnWokeUp` after `Resume`. The exit reason comes from `App.SessionEndingReason` into `BeginExit(WatchStop)`. Tested in `WatchMessageTests` (each reason), `MonitoringManagerTests` and `ChatNotificationServiceTests` (fake clock).
- **Heartbeat (#170):** `ChatChannel.HeartbeatEvery`/`HeartbeatHours`/`HeartbeatAt`/`HeartbeatPingUrl` (inside the encrypted channel list; the ping URL is in `SettingsDiff.SecretNames`). `HeartbeatSchedule.Next(after, zone)` gives the next local time, across daylight saving changes; `ChatNotificationService` keeps each channel's next one, recomputed when its schedule changes, and checks every 30 s on a `TimeProvider` timer (`CheckHeartbeats`), queueing `ChatMessageBuilder.Heartbeat` and calling `IWebhookSender.PingAsync` (a GET; `WebhookResult.FromPing`: any 2xx that isn't HTML). `HeartbeatSetup.Steps` writes the copyable steps; `HeartbeatPing.Check` the URL rules. Tested in `HeartbeatTests` (Europe/Berlin daylight saving), `ChatNotificationServiceTests` (fake clock), `ChatChannelSettingsTests` and `SettingsPageTests`.
- **Exit:** `MonitoringManager.Dispose` clears the window's `AlertsChanged` before it stops the sessions, but raises `AlertsRaised` with their ends, then `AllStopped`, before it waits for the history writer. `MainWindow.OnClosing` calls `ChatNotificationService.BeginExit(why)` first (no more status or tray updates), and `OnAllStopped` queues one *stopped watching* message per channel for everything being watched, with the alerts still active, and calls `ChannelQueue.Hurry` (a wait to retry is cut short, and nothing is retried), so the channels send while the history is saved. `Shutdown(5 s − time already spent)` then waits for what's left, off any UI dependency, and `Dispose` drops and logs the rest. Tested in `MonitoringManagerTests` and `ChatNotificationServiceTests`.
- **Sending:** `ChannelQueue`, one per channel, sends on a worker of its own, one message at a time and in order, at most one a second measured start to start; `Enqueue` never waits. The Slack or Teams JSON is written when the message is sent, so an edit to the channel applies to what's waiting and to a retry. Past 20 waiting messages, the rest fold into one `ChatMessageBuilder.Summary`, built from each message's `ChatDigest` when it's sent. `RetryPolicy` decides each retry; `NetworkGrace` is the minute after `PowerModes.Resume`. Waits run on a `TimeProvider`, so tests use `FakeTimeProvider` and never wait in real time, and are measured on its timestamps, which a change to the PC's clock doesn't move. Unit tested in `ChannelQueueTests`, `RetryPolicyTests` and `SummaryMessageTests`.
- **Status:** `ChannelStatus` (*Never used*, *Last posted*, *Failing since*). The queue reports every try as a `ChannelReport`; the service marks the channel posted or failing on the UI thread and raises `StartedFailing` only on the change into *Failing*, which the main window shows with `TrayIcon.Inform`.
- **Service:** `Desktop/Services/ChatNotificationService.cs`, owned by the main window, subscribes to `MonitoringManager.AlertsRaised`, which is raised beside the window's `AlertsChanged`. Each subscriber runs in its own try/catch, so a bug in one can't stop the other or show the connection as failing. The service takes the saved channels and connection names at startup and after every settings save; a settings file that couldn't be read just then is ignored, so it can't look like "no channels".
- **Storage:** `EncryptedChatChannels` in `settings.dat`, one DPAPI blob beside `EncryptedConnections`. Changes are logged as **Chat channel added / edited / removed** (`SettingsDiff`), with *Webhook URL* in its secret names.
- **Backstop:** `AppLog.Redact` removes any Slack, Logic Apps, Power Platform or Office webhook URL from a log line.
- The design, including what can go wrong and how each case is handled, is in [epic #108](https://github.com/aselasampath/sqlvitals/issues/108).
