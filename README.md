# Lidarr Album Art Optimizer

A Lidarr Connect plugin that prepares downloaded album artwork for
[Navidrome](https://www.navidrome.org/). After an album import, it finds
`cover.*`, `folder.*`, or `front.*`, resizes oversized artwork, recompresses it,
and writes `cover.jpg`. It also optimizes `artist.*` sidecars in the artist
root and album folders as `artist.jpg`.

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
- Creates missing `cover.jpg` files from Lidarr artwork, embedded front covers, or the MusicBrainz Cover Art Archive
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
| Optimize artist images | On | Processes `artist.*` in artist roots and album folders |
| Create missing album covers | On | Runs the fallback lookup when no supported sidecar exists |
| Use Lidarr artwork | On | Uses Lidarr's matching cached album cover first |
| Extract embedded artwork | On | Extracts the front cover from an imported audio track |
| Use Cover Art Archive | On | Looks up the monitored MusicBrainz release, then its release group |
| Dry run | Off | Logs intended changes without writing or deleting |

The scheduled scan uses the first Album Art Optimizer connection's processing
settings. Its Metadata entry supplies the scan interval.

## Missing artwork lookup

When an album folder has no `cover.*`, `folder.*`, or `front.*` sidecar, the
plugin uses this order:

1. Lidarr's cached cover for the matching album.
2. Front-cover artwork embedded in an album track.
3. Cover Art Archive artwork for the monitored MusicBrainz release.
4. Cover Art Archive artwork for the MusicBrainz release group.

Every fallback image passes through the normal compatibility pipeline. The
result is an EXIF-corrected `cover.jpg` with the configured maximum dimension
and JPEG quality; transparent pixels are flattened onto white. Original
fallback files and embedded tags are not modified.

Cover Art Archive lookup sends the MusicBrainz identifier already selected by
Lidarr. A missing response is treated as a normal lookup miss. Downloads are
limited to 32 MB and time out after 20 seconds.

If every source fails, the plugin skips the album and applies the
`missing-album-artwork` tag to its artist. A full scan removes the tag after all
of that artist's albums have resolvable artwork. No generic placeholder is
created.

To trigger lookup manually:

1. Add **Album Art Optimizer Scan** under **Settings → Metadata**.
2. Open **System → Tasks**.
3. Run **Album Art Scan**.

The completion message reports source counts and unresolved album titles.

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
