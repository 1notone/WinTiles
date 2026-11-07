# Security policy

## Reporting a vulnerability

Please **don't** open a public issue for security problems. Use GitHub's private reporting instead:
on this repository, go to **Security → Report a vulnerability**. Only the maintainers can see the report.

Include what you found, how to reproduce it, and which version you tested. Fixes are released as a new
version with a note in the [CHANGELOG](CHANGELOG.md).

## Supported versions

Only the latest release gets security fixes.

## How WinTiles handles your data

- Service passwords, tokens and API keys are stored in Windows Credential Manager, never in `layout.json`.
- `layout.json` (service addresses and user names, your tiles and settings) lives in your own `%APPDATA%` folder.
- Self-signed certificates are accepted only for services where you turn that on, and only from that service's own server.
- Keys and tokens are not sent on redirects to another server.
- There is no telemetry. See *Privacy and third-party services* in the [README](README.md) for every network connection WinTiles makes.
