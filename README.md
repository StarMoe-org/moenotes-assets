# moenotes-assets

C# / .NET 10 service and CLI for retrieving and exporting Our Notes Android assets.
It parses Addressables binary-v2 catalogs, downloads and decrypts dependencies,
and exports TextAsset content, PNG/WebP image pairs, AAC audio and VP9/H.264 MP4 video.

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

Set the **`MOENOTES_API_KEY` environment variable** before starting the HTTP server.
All POST requests, task queries and storage statistics require
`Authorization: Bearer <key>`. Without a configured key these routes return 503;
missing/incorrect credentials return 401. Browsing, manifests, files and health
checks remain public. Use HTTPS and keep the key out of public frontend code.
CLI commands operate locally and do not require an API key.

## CLI

Commands operate on the configured local store. Only one service or CLI process
can own a data directory at a time. Use HTTP while the server is running.

```sh
dotnet run --project CSharp -- refresh config.toml
dotnet run --project CSharp -- update config.toml   # check version_url, unpack new releases
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
curl -X POST http://127.0.0.1:8091/catalog/refresh -H "Authorization: Bearer $MOENOTES_API_KEY"
curl http://127.0.0.1:8091/tasks/TASK_ID -H "Authorization: Bearer $MOENOTES_API_KEY"
curl 'http://127.0.0.1:8091/assets?prefix=Live%2FMusicScore%2F&limit=20'
curl -X POST http://127.0.0.1:8091/exports \
  -H "Authorization: Bearer $MOENOTES_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"keys":["Live/MusicScore/0007/0007_03"]}'
curl http://127.0.0.1:8091/tasks/TASK_ID -H "Authorization: Bearer $MOENOTES_API_KEY"
curl http://127.0.0.1:8091/exports/EXPORT_ID
curl http://127.0.0.1:8091/files/FILE_ID -o chart.json
curl http://127.0.0.1:8091/zh-Hant/Live/MusicScore/0007/0007_03/   # files by asset path
```

POST requests return `202` and a task ID. GET never starts downloads.
See [API](docs/API.md) for selection, cancellation, manifests and file ranges.

## Chart site

`chart-site CONFIG.toml [--force] [MUSIC_ID...]` (or `POST /chart-site/build`) publishes an
[ournotes-player](https://github.com/StarMoe-org/ournotes-player) chart site at `/chart-site/`: the static stage,
notes and effects of a static base package (downloaded from `chart_base_url`, checked against `chart_base_sha256`),
and for every song its own converted chart, BGM, sound definition and jacket from this service's exports.
`chart-base NNNOTES_SITE OUT.zip SOURCE` packs an nnnotes `web` site as such a package. See [Chart site](docs/CHART_SITE.md).

## Export formats

| Input | Output |
|---|---|
| TextAsset (including gzip) | JSON, SUS, UTF-8 text, or binary payload |
| Texture2D, Sprite, populated SpriteAtlas | PNG + lossless WebP; sprite rectangle/rotation, triangle mask and split alpha |
| ACB with embedded HCA | AAC-LC M4A with cue-name metadata in its manifest |
| Full song (`Fwk.Sound.SplitAcbData`: XOR-masked ACB split across TextAssets) | Same as ACB |
| Supported USM MPEG-2 / IVF video with optional ADX/HCA | VP9 (copied) or H.264, with AAC, in MP4 |

Asset paths are resolved exactly through bundle containers. Original names are
labels, never output filesystem paths. Known unsupported types and ambiguous
labels are counted as skipped before download; decoder/data errors still fail.
USM alpha video (`@ALP`) and empty sprite atlases are only recognizable after
download and are also skipped. Skipped items are counted, not listed in results.
Raw resource containers are not advertised as successful exports.

Audio uses 96 kbps mono / 192 kbps stereo AAC without normalization. USM ADX/HCA
audio is decoded by VGAudio before AAC encoding. VP9 video is
copied without re-encoding when its IVF timing matches the USM header. Other video
uses libx264 CRF 20, medium, yuv420p and faststart; odd dimensions are padded to even.
Every output media file is probed and fully decoded before publication, except that
copied VP9 packets are counted because the source probe already decoded them. Video
frame counts/rates are checked, and available source durations are compared.

Not supported: arbitrary Unity versions/classes, models/scenes/animation, external
streaming AWB discovery, CPK, arbitrary cue playback, song-segment assembly,
multichannel audio, alpha or multi-track video. Tight sprites require supported
triangle/float-position geometry. Unsupported geometry fails explicitly.
HCA 3.0 full-band mono/stereo with noise reconstruction is supported; HCA 3.0
HFR/joint-stereo/MS/ATH modes remain explicitly unsupported. USM header frame
counts and frame rates control video timing instead of raw MPEG duration estimates.
The embedded class database can be overridden with `class_data = "/path/classdata.tpk"`.

## Storage and migration

Use a **new data directory** for this C# release. It stores `csharp.sqlite`, retained
catalog binaries, immutable exports, and temporary jobs. A directory containing
the old Rust `index.sqlite` is rejected before cleanup; automatic migration of
old tasks/exports is not implemented. This branch contains only the C# implementation;
the previous Rust implementation remains available in Git history.
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
  -e MOENOTES_API_KEY \
  -v moenotes-assets-data:/data \
  -v "$PWD/config.toml:/etc/moenotes-assets/config.toml:ro" \
  moenotes-assets:local
```

Set `listen = "0.0.0.0:8091"` and `data_dir = "/data"` in container configuration.
Export `MOENOTES_API_KEY` in the host shell before running Docker. On Zeabur, add
`MOENOTES_API_KEY` as a service environment variable and redeploy. Use a long
random secret (for example, `openssl rand -hex 32`), not a TOML setting. Changing
the key requires restarting the service and invalidates the old key.
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
`/browse`, `/scan/status`, `/bundles/{id}/contents`, `/bundles`, `/assets` and
`/diffs`. Catalogs are indexed in SQLite once. The exact-content browser scans
each remote bundle once to record UnityFS and AssetBundle container paths. Missing
bundles scan in the background when serving starts, and progress survives restarts.
The first scan downloads bundle bytes but does not retain them or re-export media.
Version/region/language catalog comparisons still use the metadata index. See
[API contract](docs/API.md) and [live acceptance](docs/LIVE_ACCEPTANCE.md).

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
WAL/SHM), `catalogs/`, `blobs/`, `exports/` and `public/` (hard links to blobs; keep it
on the same filesystem). `tmp/` is transient and cleaned;
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
Content-addressed outputs deduplicate disk bytes. Verified plaintext dependencies
and matching conversion inputs also reuse decoding across snapshots. First
downloads are still required. Metadata candidate
hashes alone never authorize reusing unverified remote content.

## Version tracking

Set `version_url` to the metadata service's `current_version.json` (for example
`https://metadata.bdon.moe/current_version.json`). While serving, the service checks it
every `version_poll_secs` (default 600, `0` disables polling). Each configured region is
matched to the document entry `metadata_region` (default: the region ID, so TW needs
`metadata_region = "hk-tw-mo"`; `""` leaves a region untracked). When an entry's
`resource_version` or `server.cdnRoot` differs from the region's last release, or that
release failed, a release is queued: one all-language batch that refreshes each catalog
from the entry's CDN roots (`a|b` mirrors are tried in order) and exports it. Later manual
refreshes of that region also use those roots instead of `cdn_root`.

When the batch ends the release is finalized and public JSON under `/versions/` is
rewritten: `current_version.json` (latest completed release per region, plus pending ones),
`index.json` (every release), and per release `{region}/{resource_version}/release.json`
and `diff/{locale}.json`, which compares published files with the previous release.
`POST /versions/check[?force=true]` (administrative) checks at once;
`update CONFIG.toml [--force]` does the same from the CLI and waits for queued releases.
The first detection on an empty store unpacks every tracked region in full.

## One-request all-language processing

`POST /tasks/batches` with `{"region":"tw"}` and the administrative Bearer key
queues refresh + export for every configured language. Batches run FIFO, one at
a time. Use `{"region":"tw","export":false}` for catalog-only indexing.
Inspect `/tasks/batches/{id}` and its child `/tasks/{task_id}` progress; cancel at
`POST /tasks/batches/{id}/cancel`. All queue routes require `MOENOTES_API_KEY`.

Queue state lives in SQLite under `/data`; unfinished batch steps resume after
restart while completed steps/exports are reused. Failed languages do not stop
the remaining languages. Direct single-language endpoints retain their existing
concurrency behavior. Zeabur logs show batch language/phase and throttled export
progress with success/failure counts. See [API](docs/API.md) for limits and details.
