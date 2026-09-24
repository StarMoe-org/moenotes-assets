# Live acceptance — 2026-09-24

Scope: Windows, .NET SDK 10.0.401. Endpoint configuration was read from the supplied
`D:\Download\nnnotes.py`; the script was not executed. Following the requested
single-region approach, actual catalogs and sample bundles were downloaded only
from TW. No full-game download or complete remote-file availability audit ran.
An earlier HEAD-only probe returned 200 for TW/EN/KR catalog URLs, which does not
prove equality of their contents. No publisher payloads are committed.

## Inventory and overhead

TW `main` catalogs for zh-Hans, zh-Hant, en, ko and ja were downloaded and indexed.
Each contains 13,359 bundle/dependency records, including 13,329 remote records
and 30 local dependencies, and 44,630 resource keys. Each language declares about
8.25–8.28 GB of source data; that source data was not bulk downloaded. The five
catalog binaries have distinct SHA256 values and total 42,755,655 bytes (40.8 MiB).

The current SQLite main database is 77,156,352 bytes (73.6 MiB), versus about
408 MiB in the initial unshared index. It retains 66,795 bundle memberships but
only 14,631 unique bundle definitions and 45,970 unique asset definitions.
WAL/SHM and future catalog history are additional costs. Startup retained all five
snapshots and did not need another refresh. Temporary reservations and directory
entries were both zero after the sample work and restart.

TW zh-Hant → en stable-key diff: 0 added, 0 removed, 326 changed, 12,991 unchanged,
5 ambiguous, 0 unknown. Counts describe key groups, not raw records. Most evidence
is catalog metadata, not observed bytes. One post-restart diff request took
269 ms on this machine; this is a single local measurement, not a latency bound.
Historical-version and cross-region diff behavior was covered by synthetic tests;
no previous live game version or second region's catalog body was compared.

## Download and export samples

A 2,560-byte bundle was verified independently for TW zh-Hant and en:

- Key: `adv_assets_adv_episode_adv_script_afterlive_10224_adv_script_afterlive_10224-episode.bundle`
- Catalog Hash128: `2f8f203648d17e342a2b257c5c50e80d`
- Original SHA256: `fbc3745773097c64802083705fcd1a39f080eecc8d7a176920ec5e4059d113ef`
- Plaintext SHA256: `b729bca86f1d618a11fc42cb74171d0568c0421f608921e97402dc7f8c617611`

Both observed hashes matched between the two language snapshots. Temporary sources
were released. This observation applies to the sample, not every candidate match.

`Live/MusicScore/0001/0001_00` exported successfully for both language snapshots.
Independent manifests reference one 3,476-byte physical blob: 6,952 logical bytes,
3,476 referenced bytes, 3,476 bytes saved. After restarting the current Release
service, `/files/{id}` returned HTTP 200 and all 3,476 bytes. `/` returned 404 as
required for API-only deployment. Region, catalog, bundle, asset, diff and storage
GET routes were exercised against the retained real index.

## Build and boundaries

Locked restore and Release build passed with zero warnings/errors. All 23 tests
passed, including graph cycles/ambiguity, scoped pointers, hash conflicts, output
deduplication, API/CORS, cleanup and the synthetic extraction/media pipeline.

The catalog inventory is complete for the five downloaded files; it does not
establish that every CDN object is present or every Unity class can be exported.
The 30 local dependencies require the relevant APK/game files for complete source
coverage; this service does not currently resolve those embedded files. Docker
packaging is provided, but container execution was not tested because the local
Docker daemon was not running. Final artifacts and history are not auto-evicted.

## Live failure investigation

The reported first 80 items contained 35 failures. All 35 were reproduced locally.
After the fixes, 15 previously failing items exported successfully (9 audio banks,
5 textures, 1 video); 20 unsupported object types were explicitly skipped, not
claimed as successful files. Each audio bank's first waveform was also decoded
with independent vgmstream r2117: PCM lengths matched and maximum 16-bit sample
difference was one unit. The USM contains 450 frames at 30 fps (15 seconds), while
raw MPEG probing incorrectly estimated 1.13 seconds. Container timing now drives
encoding and validation. No game payloads or external validation tools are shipped.

A subsequent pass over all 80 items produced 59 exports, 20 skips and one local
Windows file-sharing error; this transient error remains subject to retest. This
is sample validation, not a claim that the full game inventory is exportable.

The full local run exposed raw .resS texture payloads accidentally matching the
library's serialized-file heuristic. The worker now honors UnityFS directory bit
0x04 instead. Fourteen affected images exported PNG/WebP successfully on retest.
Song previews also use CriSerializedBytesAssetImpl with ACB bytes inside Unity
managed references; the worker now resolves the selected implementation by rid
and decodes its bytes rather than demanding a separate CRI provider dependency.
Resource limits are now sampled by the parent, which can terminate the worker
process tree; the child retains parent-liveness monitoring.
