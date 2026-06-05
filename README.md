# Cameo-IFV

**I**nstaller **F**or **V**ariants — a multi-mod launcher and incremental updater for
OpenRA-based total conversions (Cameo, Combined Arms, and sister projects).

Named after the Red Alert 2 IFV: one chassis, many payloads. One launcher, many mods.

## Why this exists

The previous `cameo-launcher` re-downloaded the entire `-x64-winportable.zip` (hundreds of
MB to GBs) for **every** release, even when almost nothing changed between two playtests.
Cameo-IFV fixes that and generalises the launcher so any OpenRA mod is just a config entry.

## Design decisions (locked)

- **Incremental updates via zsync.** Each release publishes a tiny `.zsync` control file next
  to its zip. The client range-fetches only the changed blocks of the archive, seeded from the
  copy it already has. Consecutive OpenRA releases share most bytes, so updates become small.
  *Fallback:* if a release has no `.zsync`, the launcher does a full download.
- **Windows-first, OS-agnostic core.** Ships Windows today, but the core has no `.bat`/Windows
  assumptions; platform is a key into a per-OS asset filter, so Linux/macOS is a later config flip.
- **Multi-mod by config.** A mod is data: an id, a display name, and one or more release sources
  (repo + channel + per-OS asset filter). See [config/catalog.default.json](config/catalog.default.json).
  - Cameo: a single feed at `cameo-mod/Cameo-mod` (no separate dev channel).
  - Combined Arms: stable at `Inq8/CAmod`, dev at `darkademic/CAmod`.
- **Per-version instances.** Each installed version is isolated, so versions don't clobber one
  another's files or settings.
- **Writable per-user data dir.** Settings/cache live under a per-user location (not the install
  dir), fixing the old "unwritable under Program Files" bug.

## Status

Early scaffold. See [docs/DESIGN.md](docs/DESIGN.md) for the architecture and open questions.

## Layout

- `src/CameoIFV.Core` — OS-agnostic domain: catalog/config model, release providers, zsync client,
  instance management.
- `src/CameoIFV.App` — Avalonia UI.
- `config/` — default mod catalog.

## Building

```
dotnet build Cameo-IFV.slnx
```

## Releasing

GitHub Actions publishes a Windows x64 portable zip when a version tag is pushed:

```
git tag v0.1.0
git push origin v0.1.0
```

The release asset is named like `Cameo-IFV-v0.1.0-win-x64.zip` and contains one
top-level `Cameo-IFV/` folder.
