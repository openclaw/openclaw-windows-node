// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace OpenClaw;

internal sealed partial class OpenClawPage : ListPage
{
    public OpenClawPage()
    {
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "OpenClaw";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        return [
            new ListItem(new OpenUrlCommand("openclaw://dashboard"))
            {
                Title = "🦞 Open Dashboard",
                Subtitle = "Open OpenClaw web dashboard"
            },
            new ListItem(new OpenUrlCommand("openclaw://dashboard/sessions"))
            {
                Title = "💬 Dashboard: Sessions",
                Subtitle = "Open the sessions dashboard"
            },
            new ListItem(new OpenUrlCommand("openclaw://dashboard/channels"))
            {
                Title = "📡 Dashboard: Channels",
                Subtitle = "Open the channel configuration dashboard"
            },
            new ListItem(new OpenUrlCommand("openclaw://dashboard/skills"))
            {
                Title = "🧩 Dashboard: Skills",
                Subtitle = "Open the skills dashboard"
            },
            new ListItem(new OpenUrlCommand("openclaw://dashboard/cron"))
            {
                Title = "⏱️ Dashboard: Cron",
                Subtitle = "Open the scheduled jobs dashboard"
            },
            new ListItem(new OpenUrlCommand("openclaw://chat"))
            {
                Title = "💬 Web Chat",
                Subtitle = "Open the OpenClaw chat window"
            },
            new ListItem(new OpenUrlCommand("openclaw://send"))
            {
                Title = "📝 Quick Send", 
                Subtitle = "Send a message to OpenClaw"
            },
            new ListItem(new OpenUrlCommand("openclaw://setup"))
            {
                Title = "🧭 Setup Wizard",
                Subtitle = "Open QR, setup code, and manual gateway pairing"
            },
            new ListItem(new OpenUrlCommand("openclaw://commandcenter"))
            {
                Title = "🧭 Command Center",
                Subtitle = "Open gateway, tunnel, node, and browser diagnostics"
            },
            new ListItem(new OpenUrlCommand("openclaw://healthcheck"))
            {
                Title = "🔄 Run Health Check",
                Subtitle = "Refresh gateway or node connection health"
            },
            new ListItem(new OpenUrlCommand("openclaw://check-updates"))
            {
                Title = "⬇️ Check for Updates",
                Subtitle = "Run a manual GitHub Releases update check"
            },
            new ListItem(new OpenUrlCommand("openclaw://activity"))
            {
                Title = "⚡ Activity Stream",
                Subtitle = "Open recent tray activity and support bundle actions"
            },
            new ListItem(new OpenUrlCommand("openclaw://history"))
            {
                Title = "📋 Notification History",
                Subtitle = "Open recent OpenClaw tray notifications"
            },
            new ListItem(new OpenUrlCommand("openclaw://settings"))
            {
                Title = "⚙️ Settings",
                Subtitle = "Configure OpenClaw Tray"
            },
            new ListItem(new OpenUrlCommand("openclaw://logs"))
            {
                Title = "📄 Open Log File",
                Subtitle = "Open the current OpenClaw Tray log"
            },
            new ListItem(new OpenUrlCommand("openclaw://log-folder"))
            {
                Title = "📁 Open Logs Folder",
                Subtitle = "Open the OpenClaw Tray logs folder"
            },
            new ListItem(new OpenUrlCommand("openclaw://config"))
            {
                Title = "🗂️ Open Config Folder",
                Subtitle = "Open the OpenClaw Tray configuration folder"
            },
            new ListItem(new OpenUrlCommand("openclaw://diagnostics"))
            {
                Title = "🧪 Open Diagnostics Folder",
                Subtitle = "Open the OpenClaw Tray diagnostics JSONL folder"
            },
            new ListItem(new OpenUrlCommand("openclaw://support-context"))
            {
                Title = "📋 Copy Support Context",
                Subtitle = "Copy redacted Command Center support metadata"
            },
            new ListItem(new OpenUrlCommand("openclaw://debug-bundle"))
            {
                Title = "🧰 Copy Debug Bundle",
                Subtitle = "Copy support context plus port, capability, node, channel, and activity diagnostics"
            },
            new ListItem(new OpenUrlCommand("openclaw://browser-setup"))
            {
                Title = "🌐 Copy Browser Setup",
                Subtitle = "Copy browser.proxy and node-host setup guidance"
            },
            new ListItem(new OpenUrlCommand("openclaw://port-diagnostics"))
            {
                Title = "🔌 Copy Port Diagnostics",
                Subtitle = "Copy gateway, browser proxy, tunnel ports, owners, and stop hints"
            },
            new ListItem(new OpenUrlCommand("openclaw://capability-diagnostics"))
            {
                Title = "🛡️ Copy Capability Diagnostics",
                Subtitle = "Copy permissions, allowlist health, and parity diagnostics"
            },
            new ListItem(new OpenUrlCommand("openclaw://node-inventory"))
            {
                Title = "🖥️ Copy Node Inventory",
                Subtitle = "Copy connected node capabilities, commands, and policy status"
            },
            new ListItem(new OpenUrlCommand("openclaw://channel-summary"))
            {
                Title = "📡 Copy Channel Summary",
                Subtitle = "Copy channel health and start/stop availability"
            },
            new ListItem(new OpenUrlCommand("openclaw://activity-summary"))
            {
                Title = "⚡ Copy Activity Summary",
                Subtitle = "Copy recent tray activity for troubleshooting"
            },
            new ListItem(new OpenUrlCommand("openclaw://extensibility-summary"))
            {
                Title = "🧩 Copy Extensibility Summary",
                Subtitle = "Copy channel, skills, and cron dashboard surface guidance"
            },
            new ListItem(new OpenUrlCommand("openclaw://restart-ssh-tunnel"))
            {
                Title = "🔁 Restart SSH Tunnel",
                Subtitle = "Restart the tray-managed SSH tunnel when enabled"
            }
        ];
    }
}

