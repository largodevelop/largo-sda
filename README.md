# Largo SDA

Open-source Steam Desktop Authenticator for Windows: Steam Guard codes, trade and market confirmations.

![Largo SDA](docs/screenshot.png)

## Download

[LargoSDA.exe](https://github.com/largodevelop/largo-sda/releases/latest) — one file, no installation, no .NET needed.

## Features

- Steam Guard codes for all accounts, synced with Steam time
- Trade and market confirmations
- Add an account: link a new mobile Guard by login and password, or import a `.maFile`
- Merge another `maFiles` folder by dragging it onto the window
- Copy `login:password` (passwords are encrypted with Windows DPAPI)
- Compatible with SDA `.maFile` (unencrypted)

The app keeps its accounts in the `maFiles` folder next to `LargoSDA.exe`.
Keep a backup of it and never share it.

## Build

```bash
dotnet publish src/LargoSda.App -c Release -r win-x64 -o publish
```

## License

MIT. JetBrains Mono font — SIL OFL 1.1.
