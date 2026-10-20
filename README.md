# Lidarr Album Art Optimizer

A Lidarr Connect plugin that prepares downloaded album artwork for
[Navidrome](https://www.navidrome.org/). After an album import, it finds
`cover.*`, `folder.*`, or `front.*`, resizes oversized artwork, recompresses it,
and writes `cover.jpg`.

Navidrome's default `CoverArtPriority` starts with `cover.*`, so the optimized
sidecar wins over `folder.*`, `front.*`, embedded art, and external providers.

## Features

- Runs automatically after a successful Lidarr album import
- Defaults to a 1400 px long edge and JPEG quality 75
- Preserves aspect ratio and never upscales by default
- Converts PNG and WebP sidecars to JPEG
- Uses an atomic temporary-file replacement
- Can remove the source `folder.*` or `front.*` after a successful write
- Includes dry-run mode
- Provides a scheduled/manual library scan for existing albums
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
5. To backfill existing albums, also open **Settings → Metadata**, add
   **Album Art Optimizer Scan**, and save it. The task appears in
   **System → Tasks** and defaults to a 24-hour interval.

## Settings

| Setting | Default | Description |
|---|---:|---|
| Max dimension | 1400 px | Maximum width or height |
| JPEG quality | 75 | Matches Navidrome's default resized-art quality |
| Normalize to `cover.jpg` | On | Uses Navidrome's highest-priority filename |
| Remove redundant sidecars | On | Removes the selected `folder.*`/`front.*` after success |
| Convert PNG/WebP to JPEG | On | Produces a compact, broadly compatible sidecar |
| Only shrink | On | Never enlarges small artwork |
| Scan interval | 1440 minutes | Existing-library scan frequency |
| Dry run | Off | Logs intended changes without writing or deleting |

The scheduled scan uses the first Album Art Optimizer connection's processing
settings. Its Metadata entry supplies the scan interval.

## Navidrome

The plugin is designed for Navidrome's default:

```toml
CoverArtPriority = "cover.*, folder.*, front.*, embedded, external"
CoverArtQuality = 75
```

After a bulk scan, ask Navidrome to resolve existing folder artwork again:

```bash
navidrome artwork reprocess --source folder --dry-run
navidrome artwork reprocess --source folder
```

For a specific album, use:

```bash
navidrome artwork explain al <album-id> --live
navidrome artwork refresh al <album-id>
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
