# Combined Arms zsync Watcher

Design for adding incremental-update sidecars for Combined Arms releases that are published in
repositories we do not control:

- Stable/final releases: `Inq8/CAmod`
- Dev/pre-release builds: `darkademic/CAmod`

Cameo can publish `.zsync` files in its own release workflow. CA cannot, unless upstream accepts
the same packaging change. The watcher is the fallback: a workflow in a repo we own downloads CA
release zips, generates `.zsync` files that point back to the original upstream zips, and publishes
only the sidecars plus an index.

## Goals

- Do not rehost CA zips for the MVP.
- Do not depend on upstream changing their workflows.
- Keep Cameo-IFV's catalog model generic: a source may have sidecars in the same GitHub release,
  or in a separate sidecar index.
- Make sidecar freshness auditable so the launcher never applies a `.zsync` generated for a
  replaced upstream asset.

## Non-Goals

- No binary patch format beyond zsync.
- No mirror of full CA releases unless GitHub range requests become unreliable.
- No automatic mutation of upstream releases.

## Proposed Repo

Create a small repo under the `cameo-mod` org, for example:

```text
cameo-mod/openra-mod-zsync
```

It contains:

```text
.github/workflows/combined-arms.yml
sidecars/
  combined-arms/
    stable/
      index.json
      CombinedArms-1.08.2-x64-winportable.zip.zsync
    dev/
      index.json
      CombinedArms-1.09-DevTest-11-x64-winportable.zip.zsync
scripts/
  generate-zsync-sidecars.ps1
```

The repo only stores small `.zsync` metadata and JSON indexes. The large zip remains hosted by
`Inq8/CAmod` or `darkademic/CAmod`.

## Workflow

Run on a schedule and manually:

```yaml
name: Combined Arms zsync sidecars

on:
  workflow_dispatch:
  schedule:
    - cron: "17 */6 * * *"

permissions:
  contents: write

jobs:
  combined-arms:
    runs-on: ubuntu-22.04
    steps:
      - uses: actions/checkout@v4

      - name: Install tools
        run: |
          sudo apt-get update
          sudo apt-get install -y zsync jq

      - name: Generate sidecars
        shell: pwsh
        run: |
          ./scripts/generate-zsync-sidecars.ps1 `
            -Source stable=Inq8/CAmod `
            -Source dev=darkademic/CAmod `
            -Output sidecars/combined-arms

      - name: Commit changes
        run: |
          git config user.name "github-actions[bot]"
          git config user.email "github-actions[bot]@users.noreply.github.com"
          git add sidecars/combined-arms
          git diff --cached --quiet || git commit -m "Update Combined Arms zsync sidecars"
          git push
```

The script should be idempotent. If no upstream release changed, it commits nothing.

## Generation Algorithm

For each configured channel:

1. Query `https://api.github.com/repos/{owner}/{repo}/releases?per_page=30`.
2. Skip drafts.
3. For stable/final channel, select normal releases by channel policy.
4. For dev channel, select prereleases by channel policy.
5. For each release, find the Windows asset whose name ends with `-x64-winportable.zip`.
6. Check the local index for a matching record:
   - same GitHub asset id
   - same asset name
   - same asset size
   - same asset `updated_at`
7. If it matches and the `.zsync` file exists, skip.
8. Otherwise download the zip to the workflow temp directory.
9. Run:

   ```sh
   zsyncmake -e \
     -u "<upstream browser_download_url>" \
     -o "sidecars/combined-arms/<channel>/<asset_name>.zsync" \
     "<downloaded_zip>"
   ```

10. Update `index.json`.
11. Delete the downloaded zip.

## Index Schema

One `index.json` per channel keeps the launcher lookup simple:

```json
{
  "schemaVersion": 1,
  "modId": "combined-arms",
  "channel": "dev",
  "generatedAt": "2026-06-05T10:00:00Z",
  "sourceRepository": "darkademic/CAmod",
  "sidecarRepository": "cameo-mod/openra-mod-zsync",
  "assets": [
    {
      "tagName": "1.09-DevTest-11",
      "releaseId": 123456789,
      "releaseUrl": "https://github.com/darkademic/CAmod/releases/tag/1.09-DevTest-11",
      "publishedAt": "2026-06-04T15:43:04Z",
      "assetId": 987654321,
      "assetName": "CombinedArms-1.09-DevTest-11-x64-winportable.zip",
      "assetSize": 227248375,
      "assetUpdatedAt": "2026-06-04T15:45:00Z",
      "assetDownloadUrl": "https://github.com/darkademic/CAmod/releases/download/1.09-DevTest-11/CombinedArms-1.09-DevTest-11-x64-winportable.zip",
      "zsyncName": "CombinedArms-1.09-DevTest-11-x64-winportable.zip.zsync",
      "zsyncUrl": "https://raw.githubusercontent.com/cameo-mod/openra-mod-zsync/main/sidecars/combined-arms/dev/CombinedArms-1.09-DevTest-11-x64-winportable.zip.zsync",
      "zsyncGeneratedAt": "2026-06-05T10:00:00Z"
    }
  ]
}
```

If GitHub exposes a digest field for release assets, include it as `assetDigest`. If not, the
asset id + size + updated timestamp are the freshness key.

## Cameo-IFV Catalog Extension

Current Combined Arms catalog entries have `zsyncSuffix: null`. When sidecars exist, add a
sidecar index URL instead of pretending the `.zsync` lives in the upstream release:

```json
{
  "channel": "Dev",
  "repository": "darkademic/CAmod",
  "assets": {
    "windows": {
      "assetSuffix": "-x64-winportable.zip",
      "zsyncSuffix": null,
      "zsyncIndexUrl": "https://raw.githubusercontent.com/cameo-mod/openra-mod-zsync/main/sidecars/combined-arms/dev/index.json"
    }
  }
}
```

The release provider resolves the upstream zip normally, then consults the sidecar index for a
matching `tagName + assetId + assetName + assetSize + assetUpdatedAt`. If all match, the provider
adds the `zsyncUrl` to the `UpdatePlan`; if not, it falls back to full download.

## Failure Modes

- **Upstream asset replaced in place:** index freshness key stops matching; launcher ignores the
  sidecar until the watcher regenerates it.
- **Watcher outage:** launcher full-download fallback remains correct.
- **GitHub raw URL throttling:** use GitHub release assets in the sidecar repo instead of raw
  files, or publish a small static site with the same index shape.
- **Upstream disables range requests:** rehost the zip in our sidecar repo release or another
  range-capable host. Current audits show GitHub release assets are range-capable.

## MVP Recommendation

Do not implement the watcher until Cameo-IFV has the normal GitHub release provider, full-download
install path, seed-cache retention, and Cameo `.zsync` support working end-to-end. When that is
done, implement this watcher as the first sister-project integration.
