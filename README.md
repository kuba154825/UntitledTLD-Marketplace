# UntitledTLD Marketplace

Official mods for **UntitledTLD**, the mod loader for *The Long Drive*.

You don't download anything here by hand. Open **UntitledTLD Marketplace** (Start Menu: type `UntitledTLD`), search for a mod and click **Install**. Updates show up there too.

## What is in this repo

| Path | What it is |
|---|---|
| `index.json` | The list the Marketplace reads: every mod, its version and its files |
| Releases | The built files (`.dll`, apps). One release per mod version, e.g. `autodrive-v1.0.0` |

Every file in `index.json` has a SHA-256 checksum. The Marketplace checks it after downloading and refuses a file that doesn't match.

The mods are plain C# and not obfuscated, so anyone who wants to see what a mod does can open its `.dll` in a .NET decompiler (dnSpy, ILSpy).

## index.json

```json
{
  "format": 1,
  "mods": [
    {
      "id": "autodrive",
      "name": "AutoDrive",
      "author": "UntitledTLD",
      "version": "1.0.0",
      "minLoader": "1.1.0",
      "description": "…",
      "tags": ["driving"],
      "files": [
        { "path": "AutoDrive.dll", "url": "https://github.com/…/releases/download/autodrive-v1.0.0/AutoDrive.dll", "sha256": "…", "size": 12345 }
      ]
    }
  ]
}
```

`path` is relative to `The Long Drive\UntitledLoader\Mods`.

## Publishing (official developers)

Mods are published with `publish.py` from the UntitledTLD source:

1. Raise the mod's `Version` in its code. A published version is never changed.
2. Run `build.sh`.
3. Run `python publish.py <this repo> kuba154825/UntitledTLD-Marketplace`.

It creates the release and updates `index.json` (no source code is published). The Marketplace shows the new version within a minute.

Only the repo's collaborators can publish. Mods are code that runs on players' PCs, so every mod here is reviewed.
