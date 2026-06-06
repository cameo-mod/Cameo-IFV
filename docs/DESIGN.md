# Cameo-IFV — Design

Working architecture for the multi-mod, incremental OpenRA launcher. This is a living
document; durable product decisions are summarized in the README.

## 1. The core problem

OpenRA mods publish each release as a single monolithic `-x64-winportable.zip`. A naive
launcher must re-download the whole archive every time. Prism Launcher avoids this for
Minecraft because Mojang publishes a *per-file manifest* (path + SHA + URL) plus a shared
content-addressed object store, so an update only pulls changed files. We do **not** control
how OpenRA packages its zip enough to adopt the full manifest model cheaply, so we use:

## 2. Update strategy: zsync

[zsync](http://zsync.moria.org.uk/) lets a client reconstruct a new version of a large file by
downloading only the byte ranges that differ from a local "seed" copy, using HTTP range
requests against the already-hosted file. The publisher only has to generate a small `.zsync`
control file (block checksums) per release — no per-file blob hosting.

```
publisher CI:  build zip ──► zsyncmake zip ──► upload zip + zip.zsync to the release
client update: fetch zip.zsync ──► diff vs local seed (previous zip) ──► range-GET changed blocks
               ──► assemble new zip ──► verify whole-file checksum ──► extract to new instance
```

### Viability — VERIFIED (Codex K1 audit, 2026-06-05)

zsync only saves bandwidth if **unchanged compressed entries are reusable** between two builds.
Confirmed against real releases by range-fetching only the central directories and comparing:

- Cameo `playtest-20260530 → 20260531`: 29,367 / 29,413 common entries identical by
  path+CRC+sizes+method → **~99.3% reusable compressed payload** (967.4 MB of 974.7 MB).
- Combined Arms `1.09-DevTest-10 → 11`: **~97.4% reusable** (219.6 MB of 225.5 MB).

The zip is standard, non-solid, per-entry deflate (`zip -r -9`), which is favourable. It is *not*
perfectly deterministic — `zip -r` doesn't sort entries and build steps stamp release-time mtimes,
so zip local headers + the central directory churn (~3.15 MB CD for Cameo). zsync's rsync-style
rolling scan still finds the identical compressed blocks at shifted offsets, so the savings hold;
the churn is a small constant overhead, not a blocker. **Decision: proceed with zsync; do NOT fall
back to the manifest/object-store model.**

Optional future determinism improvements (publisher side, non-blocking): sort the file list
(`find … | LC_ALL=C sort | zip -@`), `zip -X` to strip extra attrs, normalise mtimes on copied
source data. These shrink the churn overhead but are not required for the MVP.

### Seed selection — DECIDED

The client seeds from the **previously downloaded `.zip`**, which the cache therefore retains.
Reconstructing a synthetic zip (local headers + compressed data) from an *extracted* install is
not viable for the MVP, so we do not attempt it. Cost: the cache holds the most recent zip per
mod/channel (~1 GB for Cameo) as the seed for the next update; older seeds can be pruned.

### CA coverage (handoff item K3)

We don't own `Inq8/CAmod` or `darkademic/CAmod`, so they won't run our `zsyncmake` step.
Options, cleanest-first:
1. Upstream adds the step themselves.
2. A GitHub Action in a repo *we* own watches their releases, downloads each zip, runs
   `zsyncmake`, and republishes the `.zsync` (and possibly re-hosts the zip if cross-repo range
   requests are unreliable).
Until one exists, CA releases use the full-download fallback (still correct, just not incremental).

## 3. Client components (CameoIFV.Core)

- **Catalog/config** (`Model/ModCatalog.cs`) — **DONE.** Mods, sources, launch executables, and
  per-OS asset filters are data-driven through `config/catalog.default.json`.
- **Release provider** — **DONE.** `Github/GitHubReleaseProvider` lists releases with ETag /
  `If-None-Match` conditional requests (304 reuses a cached body via `Github/ETagStore` — no quota
  spend, no transfer; degrades to last-known list on rate-limit/transient errors). Selects the
  per-platform asset + optional `.zsync` sidecar into `Github/ResolvedRelease`. Tested: asset/sidecar
  selection, draft skipping, newest-first ordering, unsupported-platform, and the 304-reuse path.
  *Known limitation:* single page (`per_page=30`); pagination is a later add.
- **zsync client** — **C1 DECIDED:** depend on `zsyncnet` (MIT, NuGet 0.1.9, .NET 6+ so fine on net8.0)
  for the algorithm, but supply our own `IRangeDownloader` for transport. zsyncnet is stale (last
  release 2022, 1★) but MIT, so if it rots we vendor it; the .zsync format is stable. zsyncnet exposes
  exactly the seam we need: `Zsync.Sync(ControlFile, List<Stream> seeds, IRangeDownloader, Stream
  workingStream, IProgress<ulong>, CancellationToken)`. Implemented in `CameoIFV.Core/Update/`:
  - `GitHubRangeDownloader : IRangeDownloader` — redirect-following `HttpClient`, range requests.
    **Verified** against the live 980 MB Cameo asset (zip magic at offset 0; exact 1 KiB mid-range).
  - `IUpdater` + `UpdatePlan`/`UpdateProgress` — launcher-facing abstraction; never references zsyncnet.
  - `ZsyncUpdater` (incremental; compiles, **not yet e2e-tested** — needs a published .zsync, K2) and
    `FullDownloadUpdater` (fallback). `UpdaterFactory` picks zsync only when a control file and an
    existing seed zip are present; first installs and missing-seed updates use full download.
- **Instance manager and storage** — **DONE.** Settings and the ETag cache remain under
  `%LocalAppData%/Cameo-IFV`; players can choose and save library roots containing
  `seeds/{mod}/{channel}.zip`, `instances/{mod}/{tag}/`, and `downloads/`. `InstanceManager`
  lists/launches/deletes versions while offline. Each completed install stores a small
  `.cameo-ifv-install.json` metadata file so later launches can report their source/channel.
- **Download/patch task** — **DONE.** Uses zsync when a sidecar and seed are available, otherwise
  streams a full download. Supports cancellation, progress/byte reporting, truncated-download
  rejection, archive structural verification, and detailed session-log events.

## 4. UI (CameoIFV.App)

Avalonia MVVM (CommunityToolkit.Mvvm). **Shipped and interactively tested:** sorted mod picker,
per-source channel picker, available-releases list with publication date/relative age and incremental
marker, installed list, Install/Update + Cancel + Play + Delete, saved library-path selector, status
line, progress/byte reporting, and a selectable read-only Session log panel. The log records download
URLs, archive/staging/final paths, overwrite decisions, archive/extracted sizes, and launch details.
Autoscroll pauses when the user scrolls upward and resumes only when they return to the bottom.

`Services/LauncherServices` is the composition root (HttpClient, paths, provider, orchestrator,
instance manager); the catalog is copied to output and loaded at start. The launcher remembers the
last selected mod/channel and defensively falls back when saved catalog entries no longer exist.

### Install pipeline (CameoIFV.Core/Install) — DONE

`InstallOrchestrator.InstallAsync` runs phases Downloading → Verifying → Extracting → Finalizing → Done:
pick updater (seed-gated), assemble zip, verify it opens as a valid archive, extract into a sibling
staging directory, then swap the staged tree into `instances/{mod}/{tag}/` only after extraction
succeeds. Reinstalls preserve the prior working instance if verification/extraction fails. The
assembled zip is promoted to the seed slot only after the instance swap succeeds.

Cancellation/failure removes the temporary `.part` and staging tree. Startup/library-switch cleanup
removes abandoned `.part`, direct `*.staging-*`, and direct `*.backup-*` artifacts while preserving
real installs and nested game content. `ExecutableLocator` finds the configured `LaunchExecutable` or
an unambiguous lone top-level `.exe`; `InstanceManager` lists/launches/deletes installed instances.
The offline Core test suite covers successful/corrupt/reinstall-safe installs, executable discovery,
instance enumeration, settings compatibility, cleanup, confirmation timing, and provider behavior.

## 5. Remaining work

- ~~Seed selection / keep zip vs reconstruct from tree.~~ **Resolved:** keep the previous `.zip` as
  the seed (see §2 "Seed selection").
- ~~GitHub release-asset CDN range-request support (C3).~~ **Verified 2026-06-05:** assets return
  `206 Partial Content` + `Accept-Ranges: bytes` + correct `Content-Range` after the 302 redirect.
- End-to-end zsync validation still needs two published zsync-enabled Cameo releases: install the
  first as the seed, then confirm the second transfers only changed ranges.
- Installed game files are isolated under `instances/{mod}/{tag}/`, but OpenRA settings/support
  data are not currently isolated per instance. Cameo-IFV does not pass `Engine.SupportDir`, so
  OpenRA normally uses its shared platform support directory (for example `%AppData%\OpenRA` on
  Windows). Decide whether to preserve shared settings or offer per-instance support data.
- **Shared packages (`packageId`).** The three OpenRA entries currently point to
  the same Windows portable zip but install independently. Add optional `packageId` so UI identity
  remains per-game while download seed and extracted instance storage can be shared. Existing mods
  default `packageId` to `id`. Shared-package delete semantics need explicit UI wording.
- **Split catalog files.** Replace the growing monolithic
  `config/catalog.default.json` with a small manifest plus one file per mod/project.
- GitHub release pagination (`per_page=30`) remains to be implemented.
- Self-update of the launcher itself (deferred).
