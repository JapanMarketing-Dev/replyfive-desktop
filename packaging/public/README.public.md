# ReplyFive for Windows and Linux

ReplyFive turns a few words into a ready-to-send reply that fits the conversation you are looking at, and writes it straight into the field you are in. This repository holds the source of the Windows / Linux desktop client (C# / .NET 10 + Avalonia 12), published so that Flathub and other package repositories can build it from source. See [LICENSE](LICENSE) for what you may do with it.

- Product and pricing: https://replyfive.app
- Privacy: https://replyfive.app/legal/privacy · Terms: https://replyfive.app/legal/terms
- Contact: https://replyfive.app/legal/tokushoho

The client needs a ReplyFive account (free trial, then a paid plan). It never sends a message for you and never stores conversation text on the server.

## Build

```sh
dotnet test tests/ReplyFive.Core.Tests                                  # contract fixtures (shared/fixtures) and core logic
dotnet build src/ReplyFive.Desktop -f net10.0                            # Linux
dotnet build src/ReplyFive.Desktop -f net10.0-windows10.0.19041.0        # Windows
dotnet publish src/ReplyFive.Desktop -c Release -f net10.0 -r linux-x64 --self-contained -o out/linux-x64
```

Packaging (AppImage / .deb / Flatpak / Snap / MSIX) lives in `packaging/`. This repository is synced from the private ReplyFive monorepo; pull requests are not accepted here, but issues are welcome.
