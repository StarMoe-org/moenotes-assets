# moenotes-assets

C# / .NET 10 service and CLI for retrieving and exporting Our Notes Android assets.
It parses Addressables binary-v2 catalogs, downloads and decrypts dependencies,
and exports TextAsset content, PNG images, AAC audio and H.264 video.

This is an independent interoperability project. No game login, player credentials,
Unity Editor, Python, Rust runtime or proprietary CRI plugin is required.

## Build and run

Install the **.NET 10 SDK** and FFmpeg/ffprobe with AAC and libx264 on PATH.
Windows and Linux are supported targets.

```sh
dotnet restore MoenotesAssets.slnx --locked-mode
dotnet build MoenotesAssets.slnx -c Release
cp config.example.toml config.toml
# Set cdn_root to the authorized CDN root for your selected region.
dotnet run --project CSharp -c Release --no-build -- serve config.toml
```

The listener defaults to `127.0.0.1:8091`. All configuration fields are validated;
unknown TOML keys fail at startup. `cdn_root`, region, locale and resource version
are explicit; server discovery and game authentication are not implemented.

**There is no HTTP authentication.** Use a trusted network or an authenticated,
rate-limited reverse proxy. A caller can initiate expensive downloads/transcodes.

## CLI

Commands operate on the configured local store. Only one service or CLI process
can own a data directory at a time. Use HTTP while the server is running.

```sh
dotnet run --project CSharp -- refresh config.toml
dotnet run --project CSharp -- list config.toml 'Live/MusicScore/'
dotnet run --project CSharp -- export config.toml 'Live/MusicScore/0007/0007_03'
dotnet run --project CSharp -- export config.toml --prefix 'Live/MusicScore/'
```

`refresh` and `export` wait for completion and print the final task as JSON.
Exit codes: 0 success, 1 failure/partial export, 2 usage, 130 cancelled export.
Ctrl+C cancels active CLI work. `list` returns up to 1000 entries; use HTTP for
pagination. `--version` prints the application version.

## HTTP

```sh
curl -X POST http://127.0.0.1:8091/catalog/refresh
curl http://127.0.0.1:8091/tasks/TASK_ID
curl 'http://127.0.0.1:8091/assets?prefix=Live%2FMusicScore%2F&limit=20'
curl -X POST http://127.0.0.1:8091/exports \
  -H 'Content-Type: application/json' \
  -d '{"keys":["Live/MusicScore/0007/0007_03"]}'
curl http://127.0.0.1:8091/tasks/TASK_ID
curl http://127.0.0.1:8091/exports/EXPORT_ID
curl http://127.0.0.1:8091/files/FILE_ID -o chart.json
```

POST requests return `202` and a task ID. GET never starts downloads.
See [API](docs/API.md) for selection, cancellation, manifests and file ranges.

## Export formats

| Input | Output |
|---|---|
| TextAsset (including gzip) | JSON, SUS, UTF-8 text, or binary payload |
| Texture2D, Sprite, populated SpriteAtlas | PNG; sprite rectangle/rotation, triangle mask and split alpha |
| ACB with embedded HCA | AAC-LC M4A with cue-name metadata in its manifest |
| Supported USM MPEG-2 / IVF video with optional ADX/HCA | H.264/AAC MP4 |

Asset paths are resolved exactly through bundle containers. Original names are
labels, never output filesystem paths. Unsupported or ambiguous inputs fail;
raw resource containers are not advertised as successful exports.

Audio uses 96 kbps mono / 192 kbps stereo AAC without normalization. Video uses
libx264 CRF 20, medium, yuv420p and faststart; odd dimensions are padded to even.
Every output media file is probed and fully decoded before publication. Video
frame counts/rates are checked, and available source durations are compared.

Not supported: arbitrary Unity versions/classes, models/scenes/animation, external
streaming AWB discovery, CPK, arbitrary cue playback, song-segment assembly,
multichannel audio, alpha or multi-track video. Tight sprites require supported
triangle/float-position geometry. Unsupported geometry fails explicitly.
The embedded class database can be overridden with `class_data = "/path/classdata.tpk"`.

## Storage and migration

Use a **new data directory** for this C# release. It stores `csharp.sqlite`, retained
catalog binaries, immutable exports, and temporary jobs. A directory containing
the old Rust `index.sqlite` is rejected before cleanup; automatic migration of
old tasks/exports is not implemented. The original Rust sources remain in `src/`
and `tests/` as a reference, but default build, CI and Docker now use C#.
The new API omits the old `/v1` prefix; see the API document for exact routes.

Concurrent requests share downloads and exports. A cancelled caller does not
cancel another consumer. Successful outputs are reused; temporary source files
are removed when no consumer needs them. Each resource publishes atomically;
batches can partially succeed. Restart marks unfinished tasks failed and removes
unfinished temporary/unindexed publications. Resubmit to retry.

Queue, input, expansion, output, temporary storage, worker count and timeout budgets
are configurable. A worker runs in a separate process; cancellation kills its
process tree. A sampled watchdog checks worker resident memory, staging/output
sizes and parent liveness. These sampled checks can overshoot and are **not a hard
OS sandbox**. Use container memory/CPU limits and filesystem quotas for strict
boundaries. `memory_threshold` is accepted for old configuration compatibility;
AssetsTools.NET controls its own file-backed/memory behavior. Final exports have
no automatic eviction. Downloads do not implement byte-range resume or retries.

## Container

```sh
docker build -t moenotes-assets:local .
docker volume create moenotes-assets-data
docker run --rm -p 127.0.0.1:8091:8091 \
  -v moenotes-assets-data:/data \
  -v "$PWD/config.toml:/etc/moenotes-assets/config.toml:ro" \
  moenotes-assets:local
```

Set `listen = "0.0.0.0:8091"` and `data_dir = "/data"` in container configuration.
The container runs as UID/GID 65532. Bind mounts must be writable by that user.
`/health` reports liveness; `/ready` checks SQLite and writable temporary storage.

## Development

```sh
dotnet test MoenotesAssets.slnx -c Release
dotnet format MoenotesAssets.slnx --verify-no-changes
dotnet publish CSharp/MoenotesAssets.csproj -c Release -o artifacts/publish
```

Tests use generated resources and loopback HTTP, never publisher endpoints.
They cover text, PNG, sprites, atlas references, encrypted HCA/ACB and USM, shared
cancellation, restart recovery and HTTP file serving. Real publisher resources
are not included; synthetic coverage does not establish compatibility with every
live game revision. See [architecture](docs/ARCHITECTURE.md).

Project code is [MIT](LICENSE). Dependencies retain their licenses; GPL-enabled
FFmpeg/libx264 is not MIT-only. See [third-party notices](THIRD_PARTY_NOTICES.md).
No license here grants rights to game resources or trademarks.

## Bundle browser API and shared storage

The service is API-only; an external asset browser uses `/regions`, `/catalogs`,
`/bundles`, `/bundles/{id}/assets`, `/assets` and `/diffs`. Catalogs are indexed in
SQLite once. Browsing and version/region/language comparisons do not download or
unpack bundles. See [API contract](docs/API.md) and [live acceptance](docs/LIVE_ACCEPTANCE.md).

Each region can advertise all languages; refreshing one region's five language
catalogs is enough to browse that region's complete language inventory. Other
regions' snapshots are required to prove actual region differences. No automatic
cross-region equality assumption is made.

CDN endpoints are configuration, not compiled defaults. `config.example.toml`
shows region settings. In this local checkout, `config.toml` and
`config.docker.toml` have already been populated from the supplied nnnotes.py;
these files are ignored by Git. No account or player token is needed for the
current catalog test. Relative `data_dir` is resolved against the config file.

Persist the **whole `/data` directory** in Docker: `csharp.sqlite` (including live
WAL/SHM), `catalogs/`, `blobs/`, and `exports/`. `tmp/` is transient and cleaned;
keep staging on the same filesystem for atomic publication. Use the generated
`config.docker.toml` as the read-only configuration mount in the example above.
Region/language/version separation lives in SQLite and manifests, while identical
catalogs, descriptors and output bytes share physical storage. One volume allows
cross-region deduplication; separate region volumes duplicate common data. Only
one process may own a volume. Do not copy just the main SQLite file while live.

Downloaded and decrypted sources remain available while shared consumers need
them and are removed on the last release. Extraction staging is removed after
success, failure or cancellation; startup cleans crash leftovers and unreferenced
blobs. Referenced output files, manifests and catalog history are retained without
automatic eviction. Changed versions therefore still grow storage over time.
Content-addressed outputs deduplicate disk bytes after extraction; they do not
promise to skip cross-snapshot downloads or repeated decoding. Metadata candidate
hashes alone never authorize reusing unverified remote content.
