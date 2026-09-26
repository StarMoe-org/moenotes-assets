# Changelog

Versions follow MAJOR.MINOR.PATCH, with prerelease identifiers where applicable.

## 0.1.0-alpha.1 - 2026-09-24

- Add Android Addressables binary catalog snapshots and explicit CDN configuration.
- Add asynchronous HTTP export tasks, dependency downloads, bundle decryption,
  SQLite indexing, cancellation, shared work, and temporary-file cleanup.
- Export TextAsset payloads, PNG textures/sprites/atlases, HCA-based M4A audio,
  and supported USM video as H.264 MP4.
- Add immutable artifact URLs with Range, HEAD and ETag support.
- Add bounded Rust workers and local container packaging.
- Harden deterministic dependency ordering, publication rollback, conditional
  file responses, bounded gzip decoding and CRI media validation before the
  initial public source commit.

The HTTP v1 interface and Rust library are experimental in this alpha. Breaking
changes require explicit release notes; a frozen stable API is not claimed.

## 0.2.0-csharp (unreleased)

- Add the ournotes-player chart site (`chart-site`, `POST /chart-site/build`, static `/chart-site/`): a static base
  package (`chart-base` packs an nnnotes `web` site; `chart_base_url` + `chart_base_sha256` download it once) plus
  charts composed from the song's exports, with a port of the nnnotes chart converter (field-for-field equal on all
  336 TW charts), ACB cue parsing and a worker mode that returns a cue sheet's ACB.
- Reimplement the service and CLI in C# on .NET 10; default CI and container use .NET.
- Redesign HTTP routes without the `/v1` prefix; CLI refresh/export wait for completion.
- Add UnityFS/TextAsset/PNG/Sprite/atlas and embedded ACB/HCA/USM export in C#.
- Persist tasks and manifests in a separate SQLite store; retain shared work,
  cancellation, atomic publication and conditional/range file downloads.
- Reject legacy Rust data directories before cleanup; use a new directory.
- Add synthetic pipeline and loopback HTTP integration tests.
- Add API-only bundle/asset browsing, per-region/language/version snapshots,
  SQL diffs with explicit evidence and download/hash verification tasks.
- Share catalog graphs and descriptor definitions in SQLite; store final files
  by content SHA256 while retaining independent scoped manifests.
- Add configurable frontend CORS and storage statistics; retain automatic
  temporary cleanup without automatic history/output eviction.
- Validate five live TW language catalogs and small download/export samples.
- Remove Rust source, tests, Cargo/toolchain files and obsolete dependency notices
  from the C# branch; retain notices for code adapted into the C# implementation.
- Protect administrative HTTP routes with an environment-configured Bearer API
  key; disable them when unconfigured and keep browsing/files public.
- Add persistent FIFO all-language refresh/export batches, queue inspection,
  cancellation, restart recovery and throttled task progress logs.
- Fix full-band HCA 3.0 noise decoding, use USM frame timing instead of raw MPEG
  duration estimates, omit unnecessary script metadata for supported data exports,
  collapse export aliases and report unsupported inputs as skipped.
- Export PNG and lossless WebP together using in-process SkiaSharp encoding.
- Reuse completed conversions across scopes only after verifying plaintext source
  hashes and conversion inputs; retain separate manifests and report reuse counts.
- Use UnityFS serialized-entry flags to keep raw .resS pixels out of metadata
  parsing; support CRI audio embedded in Unity managed byte implementations.
- Enforce worker resource limits from the parent process so the entire child
  process tree can be terminated without attempting to kill the caller itself.
- Fix temporary-budget exhaustion cascades at high download concurrency: reserve
  each complete export's dependencies and workspace before downloading, wait for
  capacity, and retain the reservation until shared source cleanup completes.
- Do not count cancellation during deployment as a per-resource export failure.
- Copy USM VP9 video into MP4 instead of re-encoding it to H.264 when IVF timing
  matches the USM header; MPEG and mismatched-timing sources are still re-encoded.
  Video has its own profile, so previously exported images/text/audio stay reusable.
- Close the demuxed USM video before renaming it to `.ivf`, which failed on Windows.
- Decode USM ADX audio with VGAudio instead of FFmpeg. FFmpeg's ADX demuxer failed
  most mono and some stereo streams at the standard end frame ("corrupt input
  packet" / I/O error), and its decoder deviates from CRI's scale and coefficient
  arithmetic. The movie profile is now v2, so affected videos are exported again.
- Record USM alpha video (`@ALP`), which is unsupported, as skipped instead of failed.
  It is detected after download; outputs and profiles are unchanged.
- Fix the remaining TW export failures: ACB cues that reference a block sequence
  (intro/loop BGM), USM movies written without the CRI mask (plain MPEG was
  unmasked into invalid data) or without `audio_codec` in the audio header (ADX/HCA
  is identified by its magic), and tight sprite meshes of 4096px textures, whose
  per-triangle bounding boxes exceeded the rasterization budget; masks now test
  only each row's span and are pixel-identical. Empty sprite atlases are skipped.
- Count skipped export items without listing them in task `results`; a full
  catalog export's task document shrinks from megabytes of unsupported objects.
- Start listening without waiting for orphan cleanup: the scan of `blobs/` and
  `exports/` for uncommitted publications (about a minute on a large network
  volume) runs in the background, and publication holds a lock from its first
  move until its commit so the sweep never removes it.
- Fix intermittent Windows test cleanup failures: tests decoded images through
  SkiaSharp file paths, whose inheritable native handles leaked into workers
  started by parallel tests. Images are now decoded from memory.
- Export full songs (`Fwk.Sound.SplitAcbData`). Their TextAsset chunks are joined in
  serialized order and XORed with `0x5A`, as the game's SplitAcbLoader does, and the
  recovered ACB takes the existing HCA → AAC path. This adds a previously skipped
  input type; profiles and existing outputs are unchanged.
- Startup recovery reads export IDs, referenced blob hashes and unfinished tasks in
  SQL instead of deserializing every manifest, file record and task before listening.
  A `[startup]` log line reports each recovery phase's duration.
- Log ASP.NET Core framework events at Warning and above; per-request lines (four per
  file download) are no longer written. The container clears `ASPNETCORE_HTTP_PORTS`
  so the configured listener no longer triggers a port-override warning.
- Serve published files by asset path: `/{locale}/{key}/{label}{extension}`, plus a
  `/{locale}/{key}/` listing. Paths follow the newest snapshot that has published the
  key, use a 10-minute cache with the content ETag, and never start work.
- Make request paths withstand load: path resolutions (including misses), scope
  snapshot lists and file records are cached in memory, validated by per-key and
  per-catalog versions; request reads use pooled read-only WAL connections instead of
  the writer's lock; misses use one query for all candidate exports and parse UTF-8
  directly; writer-side `Get` parses JSON after releasing the lock. Path 404s are
  publicly cacheable for 60 seconds. Locally, reads during a long write went from
  97 to 3,638 requests/s (p50 2.9 s to 70 ms) and 304s stopped querying SQLite.
- Serve paths from a static tree: publication hard-links files into
  `public/{locale}/{key}/{label}{extension}` (newest snapshot wins, atomic renames,
  owner in `.export.json`) and the static file middleware serves it. Existing exports
  are backfilled in the background on first start; until then misses resolve from
  SQLite. Locally, 304s went from 9,341 to 48,838 requests/s and 200s from 3,923 to
  5,341; path 404s no longer touch SQLite.
- Give every file a path: when files with different content share a label, the first
  in export order owns `{label}{ext}` and the others are `{label}__{seq}{ext}`
  (previously neither had a path, which hid textures such as item icons and band
  logos). The tree version is bumped so existing trees are rebuilt on start.
- Give movies a path: USM exports label their MP4 with the full asset key, which
  contains `/` and so had no path (only `/files/{id}` worked). A label made of safe
  segments now contributes its last one (`Cri/Video/adv/x/x/x.mp4`); labels with an
  unsafe segment still have none. The tree version is bumped to link existing movies.
- Track resource versions: with `version_url` (the metadata service's `current_version.json`), the service checks
  every `version_poll_secs` (default 600; `POST /versions/check`, CLI `update`) and queues a release for each tracked
  region (`metadata_region`, default its id) whose `resource_version` or `server.cdnRoot` changed, or whose last
  release failed. A release is one batch that refreshes and exports every language from the entry's CDN roots (`a|b`
  mirrors tried in order; the snapshot records the root that answered); later manual refreshes follow those roots
  instead of `cdn_root`. When the batch ends, `/versions/` is rewritten: `current_version.json`, `index.json`,
  `{region}/{resource_version}/release.json` and `diff/{locale}.json`, which compares published files by content
  against the previous release (added, changed, removed, failed). `allow_insecure_version_url` permits a plain HTTP
  `version_url` for a metadata service reachable only inside a cluster network; CDN roots stay HTTPS-only.
