# VPN Subscription Updater

This project downloads VPN subscription URLs from `urls.txt`, parses valid configs, validates the servers, and writes the final healthy results to `my-sub.txt`.

## What it does

- Reads all subscription URLs from `urls.txt`
- Downloads and parses subscription content
- Extracts VMess, VLESS, and Trojan configs
- Validates each server using TCP and, when available, Xray
- Removes duplicate and invalid entries
- Writes:
  - `my-sub.txt` → Base64 final subscription
  - `valid-servers.txt` → readable plain-text list of healthy links

## Requirements

- .NET 10 SDK
- Xray binary installed (optional but recommended for stronger validation)

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
- runs every 6 hours

## Files

- `urls.txt` → input subscription sources
- `my-sub.txt` → final Base64 subscription
- `valid-servers.txt` → readable healthy server list

## Notes

The project is designed for automated GitHub-hosted updates and periodic validation of live VPN servers.
