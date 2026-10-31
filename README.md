# Lidarr Album Art Optimizer

A Lidarr Connect plugin that prepares downloaded album artwork for
[Navidrome](https://www.navidrome.org/). After an album import, it finds
`cover.*`, `folder.*`, `front.*`, `albumart.*`, `album.*`, or `artwork.*`,
resizes oversized artwork, recompresses it, and writes `cover.jpg`. It also
optimizes `artist.*` sidecars and can backfill `artist.jpg` from Lidarr's artist
metadata.

Navidrome's default `CoverArtPriority` starts with `cover.*`, so the optimized
sidecar wins over `folder.*`, `front.*`, embedded art, and external providers.
Its default `ArtistArtPriority` similarly checks `artist.*` in artist roots and
album folders before external providers.

## Features

- Runs automatically after a successful Lidarr album import
- Defaults to a 1400 px long edge and source JPEG quality 85
- Applies EXIF orientation and uses a white background when converting transparent artwork to JPEG
- Preserves aspect ratio and never upscales by default
- Converts PNG and WebP sidecars to JPEG
- Creates missing `cover.jpg` files from Lidarr artwork, embedded front covers, MusicBrainz Cover Art Archive, or an optional Discogs fallback
- Tries every known MusicBrainz release and approved Cover Art Archive front-image metadata before the release group
- Rejects shared artist/library roots while allowing an artist folder owned by one album
- Uses an atomic temporary-file replacement
- Can remove the source `folder.*` or `front.*` after a successful write
- Optimizes `artist.*` in artist roots and Navidrome's `album/artist.*` fallback
- Includes dry-run mode
- Provides a scheduled/manual library scan for existing album and artist art
- Does not rewrite embedded artwork in audio files

## Requirements

- Lidarr `3.1.2.4938` or newer on the `develop` or `nightly` branch
- Write access to the music library

Plugin support is not available on Lidarr's stable branch. Back up Lidarr before
switching branches: the database migration cannot be rolled back without
restoring that backup.

## Installation

1. In Lidarr, open **System → Plugins**.
2. Paste this repository URL:

   ```text
   https://github.com/cardner/lidarr-albumart-plugin
   ```

3. Select **Install**, then restart Lidarr.
4. Open **Settings → Connect**, add **Album Art Optimizer**, and save it.
5. To backfill existing artwork, also open **Settings → Metadata**, add
   **Album Art Optimizer Scan**, and save it. The task appears in
   **System → Tasks** and defaults to a 24-hour interval.

## Settings

| Setting | Default | Description |
|---|---:|---|
| Max dimension | 1400 px | Maximum width or height |
| JPEG quality | 85 | Preserves source quality before Navidrome creates served thumbnails |
| Normalize artwork filenames | On | Writes Navidrome-priority `cover.jpg` or `artist.jpg` |
| Remove redundant sidecars | On | Removes the selected source after successful normalization |
| Convert PNG/WebP to JPEG | On | Produces a compact, broadly compatible sidecar |
| Only shrink | On | Never enlarges small artwork |
| Scan interval | 1440 minutes | Existing-library scan frequency |
| Optimize artist images | On | Processes `artist.*` and backfills the artist root from Lidarr metadata |
| Create missing album covers | On | Runs the fallback lookup when no supported sidecar exists |
| Use Lidarr artwork | On | Uses Lidarr's matching cached or remote album cover first |
| Extract embedded artwork | On | Extracts the front cover from an imported audio track |
| Use Cover Art Archive | On | Looks up imported, monitored, and other known MusicBrainz releases, then the release group |
| Use Discogs fallback | Off | Searches authenticated Discogs only after all exact/local sources miss |
| Discogs personal access token | Empty | Secret token generated in Discogs developer settings; required when Discogs is enabled |
| Discogs release overrides | Empty | Optional `LidarrAlbumId=DiscogsReleaseId` mappings, one per line or comma-separated |
| Dry run | Off | Logs intended changes without writing or deleting |

The scheduled scan uses the first Album Art Optimizer connection's processing
settings. Its Metadata entry supplies the scan interval.

## Missing artwork lookup

When an album folder has no supported sidecar, the plugin uses this order:

1. Lidarr's cached or remote cover for the matching album.
2. Front-cover artwork embedded in an album track.
3. Cover Art Archive artwork for the imported release, monitored releases, and
   other releases known to Lidarr.
4. Cover Art Archive artwork for the MusicBrainz release group.
5. Discogs, when explicitly enabled and supplied with a personal access token.

Every fallback image passes through the normal compatibility pipeline. The
result is an EXIF-corrected `cover.jpg` with the configured maximum dimension
and JPEG quality; transparent pixels are flattened onto white. Original
fallback files and embedded tags are not modified.

Cover Art Archive lookup uses its JSON index to prefer approved 1200px front
images, then falls back to the direct front endpoint. A missing response is a
normal lookup miss. Downloads are limited to 32 MB and time out after 20
seconds.

Discogs matching is deliberately conservative. It requires exact normalized
album and artist names for regular releases and also uses year and track-list
overlap. `Various Artists` releases require title, year, and track evidence;
ambiguous title-only matches are rejected. Requests use the token in an
`Authorization` header, are limited to one request per second, and honor
Discogs authentication and rate-limit responses. Tokens and signed image URLs
are never written to logs.

For a remaining miss, find the Lidarr album ID in its URL and the numeric
Discogs release ID in the Discogs release URL, then add a mapping such as
`454=123456` to **Discogs release overrides**. Overrides skip fuzzy search but
still download and normalize the authenticated Discogs release image.

Before writing, the plugin validates the destination against the artist and
library roots. Multi-disc parents are accepted only when every track directory
is contained by the same album parent. Path comparison normalizes Unicode
apostrophes and hyphens, ignores case and leading articles, and resolves
existing symlinks, while writes continue to use the original track directory.
An artist directory or collaboration folder is accepted only when no other
monitored album stores tracks there. Shared artist directories, unrelated
artist folders, and unsafe paths such as `/Music` are reported as unresolved
rather than receiving a shared `cover.jpg`; warnings include the rejected
track directory and cause.

If every source fails, the plugin skips the album and applies the
`missing-album-artwork` tag to its artist. A full scan removes the tag after all
of that artist's albums have resolvable artwork. No generic placeholder is
created.

To trigger lookup manually:

1. Add **Album Art Optimizer Scan** under **Settings → Metadata**.
2. Open **System → Tasks**.
3. Run **Album Art Scan**.

The completion message reports per-source album and artist counts, unsafe path
rejections, and unresolved album titles. Debug logging includes per-source miss
reasons without credentials.

## Navidrome

The plugin is designed for Navidrome's default:

```toml
CoverArtPriority = "cover.*, folder.*, front.*, embedded, external"
ArtistArtPriority = "artist.*, album/artist.*, external"
CoverArtQuality = 75
```

The plugin's default JPEG quality is intentionally higher than Navidrome's default
served-art quality to reduce visible artifacts from successive JPEG encodes.

After a bulk scan, ask Navidrome to resolve existing folder artwork again:

```bash
navidrome artwork reprocess --source folder --dry-run
navidrome artwork reprocess --source folder
```

To resolve artist images again after a backfill:

```bash
navidrome artwork reprocess --kind ar --dry-run
navidrome artwork reprocess --kind ar
```

For a specific album or artist, use:

```bash
navidrome artwork explain al <album-id> --live
navidrome artwork refresh al <album-id>
navidrome artwork explain ar <artist-id> --live
navidrome artwork refresh ar <artist-id>
```

The plugin does not call Navidrome directly. It only changes sidecar files in
the shared music library.

## Build

The project targets .NET 8. Compile-time Lidarr assemblies must be placed in
`local/lidarr-ref/`:

```text
Lidarr.Core.dll
Lidarr.Common.dll
NLog.dll
FluentValidation.dll
TagLibSharp.dll
Equ.dll
System.IO.Abstractions.dll
SixLabors.ImageSharp.dll
```

Then run:

```bash
dotnet restore AlbumArt.sln
dotnet build AlbumArt.sln -c Release
dotnet test tests/Lidarr.Plugin.AlbumArt.Tests/Lidarr.Plugin.AlbumArt.Tests.csproj -c Release
```

GitHub Actions downloads the pinned Lidarr assemblies automatically.

## Releasing

Releases are built from semantic-version tags. From a clean `main` branch:

```bash
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

The release workflow builds and tests the plugin, then publishes:

```text
Lidarr.Plugin.AlbumArt.1.0.0.net8.0.zip
```

The release must remain published (not draft), and its asset name must contain
`net8.0.zip` for Lidarr's plugin installer to detect it.

## Related projects

- [CoverArtSync](https://github.com/bearinfo/Lidarr.Plugin.CoverArtSync) can
  fetch missing artwork; this plugin then optimizes artwork after imports.
- [Navidrome artwork resolution](https://www.navidrome.org/docs/usage/library/artwork/)
- [Navidrome artwork CLI](https://www.navidrome.org/docs/usage/admin/cli/artwork/)

## License

MIT
