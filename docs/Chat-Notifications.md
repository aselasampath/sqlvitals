# Chat Notifications

A Windows notification only reaches the person at the PC that runs SqlVitals. **Chat channels** send what SqlVitals sees to a **Slack** or **Microsoft Teams** channel, so the whole team sees it, even when nobody is looking at that PC.

> **Where:** ⚙️ Settings → *Alerts* → *Chat channels (Slack and Teams)* · **Since:** 0.49.0

> **Status:** from 0.49.0 you can set up channels and send a test message to each one. Posting alerts to them is being built in the versions that follow ([epic #108](https://github.com/aselasampath/sqlvitals/issues/108)), and this page grows with it.

Both services take a **webhook**: a URL you create for one channel. Anything that sends it a message appears in that channel. SqlVitals needs no app, bot, API key or account of its own.

## What you need

| Service | Plan | Notes |
|---|---|---|
| **Slack** | Any plan, the **free plan** included (it allows up to 10 apps) | A workspace can require an admin to approve new apps. Each webhook posts to one channel. |
| **Microsoft Teams** | A **Microsoft 365 work or school account** (any business plan) | The free personal Teams can't do it. No Power Automate Premium licence and no Azure subscription are needed. |

Messages are sent **only while SqlVitals is running on this PC**, over HTTPS from this PC to Slack or Microsoft. Nothing is installed or changed on your SQL Servers.

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

In **⚙️ Settings → Alerts → Chat channels (Slack and Teams)**, click **+ Add channel**:

| Field | What to enter |
|---|---|
| **Name** | What you call it, e.g. *DBA team (Teams)*. Shown in Settings, the activity log and the app log. |
| **Webhook URL** | The URL you copied. Once saved it's shown masked, e.g. `hooks.slack.com/…/x7Qa`; click **Show** to see or change it. |
| **Service** | Slack or Microsoft Teams. Picked from the URL when SqlVitals recognises it; you can change it. |
| **On** | Turn off to pause the channel without removing it. A test message can still be sent. |

Click **💾 Save channel**. The URL is checked first:

| Check | If it fails |
|---|---|
| It's a whole URL starting `https://` | Refused |
| Not an Office 365 connector (`*.webhook.office.com`) | Refused, with a link to the Teams steps |
| A Slack channel's URL is on `hooks.slack.com/services/` | Refused |
| A Teams channel's URL is on a host SqlVitals knows | **Allowed with a warning**: Microsoft has changed the host before, so send a test message to check it |
| No other channel has the same URL | Refused, naming that channel. Letter case in the host, the `:443` port and a trailing `/` don't make a URL different. |

Select a channel to edit it, or click **🗑 Remove** to remove it. The webhook itself stays in Slack or Teams until you delete it there.

## Send test message

Select a channel and click **📨 Send test message**. SqlVitals posts *"Test message from SqlVitals on &lt;PC name&gt;. Alerts will be posted here."* once, without retrying, and shows the result:

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

Each channel's row shows **Never used** or **Last posted 10:42**. The status is kept while SqlVitals runs, so it starts again at *Never used* after a restart.

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
- **Service:** `Desktop/Services/ChatNotificationService.cs`, owned by the main window. It takes the saved channels at startup and whenever a channel is added, edited or removed. A settings file that couldn't be read just then is ignored, so it can't look like "no channels".
- **Storage:** `EncryptedChatChannels` in `settings.dat`, one DPAPI blob beside `EncryptedConnections`. Changes are logged as **Chat channel added / edited / removed** (`SettingsDiff`), with *Webhook URL* in its secret names.
- **Backstop:** `AppLog.Redact` removes any Slack, Logic Apps, Power Platform or Office webhook URL from a log line.
- The design, including what can go wrong and how each case is handled, is in [epic #108](https://github.com/aselasampath/sqlvitals/issues/108).
