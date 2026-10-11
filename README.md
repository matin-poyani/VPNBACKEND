# VPN Subscription Updater

This project downloads VPN subscription URLs from `urls.txt`, parses valid configs, validates the servers, and writes the final healthy results to `my-sub.txt`.

## What it does

- Reads all subscription URLs from `urls.txt`
- Downloads and parses subscription content
- Extracts VMess, VLESS, and Trojan configs
- Validates each server using TCP and Xray
- Measures Xray RTT through a local SOCKS proxy and labels it explicitly
- Removes duplicate and invalid entries
- Replaces previously published Telegram messages with the latest healthy set
- Writes:
  - `my-sub.txt` → Base64 final subscription
  - `valid-servers.txt` → readable plain-text list of healthy links

## Requirements

- .NET 10 SDK
- Xray binary installed (required to validate servers and measure Xray RTT)

## Run locally

```bash
dotnet run
```

## GitHub Actions

The workflow in `.github/workflows/Update.yaml`:

- installs Xray
- runs the updater
- writes the output files
- commits and pushes only when the result actually changes
- runs at 06:00 and 18:00 UTC (every 12 hours); `workflow_dispatch` runs it on demand

### Telegram setup

Automatic publishing is enabled when these repository Actions secrets are configured:

- `TELEGRAM_BOT_TOKEN` → bot token from BotFather
- `TELEGRAM_CHAT_ID` → public channel username such as `@vpn_free_iranin`, or the numeric chat ID for a private channel

Add them under **GitHub repository → Settings → Secrets and variables → Actions → New repository secret**. Do not put the bot token in source files, `appsettings.json`, workflow YAML, or commit history; a private repository is not a substitute for a secret store. Add the bot to the channel as an administrator with permission to post and delete messages. Each scheduled or manually dispatched workflow run then validates the nodes and publishes the latest healthy set automatically.

## Files

- `urls.txt` → input subscription sources
- `my-sub.txt` → final Base64 subscription
- `valid-servers.txt` → readable healthy server list
- `.telegram-message-ids.json` → IDs used to remove the previous Telegram publication

The Telegram bot must be an administrator with permission to post and delete messages. Telegram only allows bots to delete messages within its supported deletion window; if an old message can no longer be deleted, publishing stops rather than creating duplicate posts. The first run after enabling tracking cannot remove messages created by earlier versions because their message IDs were not saved.

The displayed `Xray RTT` is the elapsed time for an HTTPS probe through the tested Xray configuration. It is not a guarantee of general VPN performance or availability for other destinations.

## Notes

The project is designed for automated GitHub-hosted updates and periodic validation of live VPN servers.
