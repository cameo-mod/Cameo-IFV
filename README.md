# Cameo-IFV

[![Release](https://github.com/cameo-mod/Cameo-IFV/actions/workflows/release.yml/badge.svg)](https://github.com/cameo-mod/Cameo-IFV/actions/workflows/release.yml)

*"Everything's made to fit!"*

**I**nstaller **F**or **V**ariants — a multi-mod launcher and incremental updater for
OpenRA-based games and total conversions (Cameo, Combined Arms, and sister projects). Capable of cutting down update download sizes (for compatible mods) after initial mod install.

Name inspired by RA2 Allied IFV.

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
  - You Must Construct Additional: stable at `patrickwieth/YMCA`.
  - OpenE2140: stable at `OpenE2140/OpenE2140`.
  - OpenRA: Red Alert, Tiberian Dawn, and Dune 2000 from `OpenRA/OpenRA`.
- **Per-version game files.** Each installed version is extracted into its own instance directory,
  so versions do not clobber one another's game files. OpenRA settings and support data remain
  shared unless a game package supplies its own local `Support` directory.
- **Writable per-user data dir.** Settings/cache live under a per-user location (not the install
  dir), fixing the old "unwritable under Program Files" bug.
- **Saved library locations.** Players can keep installs on another drive, save multiple library
  paths, and switch the active location without moving the launcher itself.
- **Safe installs and useful diagnostics.** Downloads can be cancelled; archives extract into a
  staging directory before replacing an existing install; interrupted scratch files are cleaned
  on startup. The in-app session log records URLs, paths, extraction details, and launch commands.

## Status

Cameo-IFV is a usable Windows-first launcher with automated portable releases. It can browse,
install, launch, and delete isolated versions from the built-in catalog. Full-download updates are
working; incremental zsync support is implemented.

## Layout

- `src/CameoIFV.Core` — OS-agnostic domain: catalog/config model, release providers, zsync client,
  instance management.
- `src/CameoIFV.App` — Avalonia UI.
- `config/` — default mod catalog.

## Building

```
dotnet build Cameo-IFV.slnx
```

Convenience scripts to support development testing (launch-*.cmd) are available.

## Releasing

GitHub Actions publishes a Windows x64 portable zip when a version tag is pushed, for example:

```
git tag v1.0.0
git push origin v1.0.0
```

The release asset is named like `Cameo-IFV-v1.0.0-win-x64.zip` and contains one top-level
`Cameo-IFV/` folder with `Cameo-IFV.exe`, `catalog.default.json`, and a player-facing
`README.txt`. Extract it and run `Cameo-IFV.exe`.
