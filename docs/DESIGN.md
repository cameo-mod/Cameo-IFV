# Cameo-IFV — Design

Working architecture for the multi-mod, incremental OpenRA launcher. This is a living
document; nothing here is sacred except the four locked decisions in the README.

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

- **Catalog/config** (`Model/ModCatalog.cs`) — mods, sources, per-OS asset filters. Done (scaffold).
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
- **Instance manager** — per-version isolated install dirs + per-user data dir for settings/cache/seed.
  Paths layer **DONE** (`Storage/LauncherPaths`): single writable per-user root
  (`%LocalAppData%/Cameo-IFV`) with `etags.json`, `seeds/{mod}/{channel}.zip` (the retained zsync
  seed), `instances/{mod}/{tag}/`, `downloads/`. The extract-to-instance + promote-temp-zip-to-seed
  orchestration is the next step. `Update/UpdatePlanner` builds the `UpdatePlan` (seed lookup +
  distinct temp output path so zsync never truncates its own seed).
- **Download/patch task** — zsync path when `.zsync` present, full download otherwise; whole-file
  checksum verification before extract; resumable.

## 4. UI (CameoIFV.App)

Avalonia MVVM (CommunityToolkit.Mvvm). **MVP shipped:** mod picker, available-releases list (tag +
channel + an "incremental" marker when a `.zsync` exists), installed list, Install/Update + Play +
Delete, status line and progress bar. `Services/LauncherServices` is the composition root (HttpClient,
paths, provider, orchestrator, instance manager); the catalog is copied to output and loaded at start.
Releases are aggregated across a mod's sources (so CA shows stable + dev). *Built + unit-tested; the
GUI itself has not been launched interactively yet.* Later: per-channel filtering UI, transferred-vs-total
byte readout to make the zsync win visible, cancellation.

### Install pipeline (CameoIFV.Core/Install) — DONE

`InstallOrchestrator.InstallAsync` runs phases Downloading → Verifying → Extracting → Finalizing → Done:
pick updater (seed-gated), assemble zip, verify it opens as a valid archive, extract to an isolated
`instances/{mod}/{tag}/`, then promote the assembled zip to the seed slot for the next diff. On failure
it deletes the temp `.part`. `ExecutableLocator` finds the run target (configured `LaunchExecutable` or a
lone top-level `.exe`). `InstanceManager` lists/launches/deletes installed instances. Tested with a
synthetic zip (happy path + corrupt-archive rejection), no network needed.

## 5. Open questions

- ~~Seed selection / keep zip vs reconstruct from tree.~~ **Resolved:** keep the previous `.zip` as
  the seed (see §2 "Seed selection").
- ~~GitHub release-asset CDN range-request support (C3).~~ **Verified 2026-06-05:** assets return
  `206 Partial Content` + `Accept-Ranges: bytes` + correct `Content-Range` after the 302 redirect.
- zsync client: maintained managed .NET library vs. implementing the rolling-checksum + range
  reassembly ourselves (Claude C1, in progress).
- Self-update of the launcher itself (deferred).
