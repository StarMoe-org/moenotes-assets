# HTTP API — C# edition

Base URL: `http://127.0.0.1:8091`. JSON uses snake_case. No `/v1` prefix
or built-in browser UI; `/` returns 404. An external frontend
can use `cors_origins = ["http://localhost:3000"]` in TOML (exact origins), or
`cors_origins = ["*"]` to allow any origin without cross-origin credentials.

## Authentication

Set `MOENOTES_API_KEY` in the server environment (Zeabur service variables, or
Docker `-e MOENOTES_API_KEY`). It is read at startup, not stored in TOML/SQLite.
All POST routes, `/tasks/{id}` and `/storage` require the header
`Authorization: Bearer <key>`. Other mutations are also denied without a key.
Missing/incorrect credentials return 401 with `WWW-Authenticate: Bearer`;
an unset/blank environment variable disables protected routes with 503.
Protected responses use `Cache-Control: no-store`. Keys in query strings or
cookies are not accepted. Change the environment variable and restart to rotate.

Catalog/asset/bundle/diff browsing, published manifests/files and health checks
remain public. CORS preflight accepts Authorization without requiring a key on
the OPTIONS request. CORS does not grant access to protected requests. Send the
key from a trusted backend/admin client over HTTPS, not public frontend code.

| Method | Path | Result |
|---|---|---|
| GET | /health, /ready | Liveness / SQLite and temporary storage readiness |
| GET | /regions | Configured IDs, default_locale and locales |
| POST | /catalog/refresh?region=tw&locale=en&version=main | 202 catalog task |
| GET | /catalogs?region=tw&locale=en | Retained snapshot statistics |
| GET | /bundles | BundlePage |
| GET | /bundles/{id} | Bundle descriptor and observed hashes |
| GET | /bundles/{id}/assets | AssetPage including transitive dependents |
| GET | /bundles/{id}/equivalents | Candidate/verified matches in retained snapshots |
| POST | /bundles/verify | 202 download/hash verification task |
| GET | /assets | AssetPage |
| GET | /diffs?from=SNAPSHOT_A&to=SNAPSHOT_B | Bundle diff |
| GET | /storage | Index, output deduplication and temporary budget statistics |
| POST | /exports | 202 export task |
| GET | /tasks/{id} | Persisted task state |
| POST | /tasks/{id}/cancel | Cancel; terminal tasks remain unchanged |
| GET | /exports/{id} | Published manifest |
| GET, HEAD | /files/{id} | File with Range, ETag and conditional requests |

## Selection and pagination

Bundle and asset routes accept `snapshot`, or `region` and `locale` to select the
current snapshot for that scope's configured version. If omitted, configuration
defaults apply. Explicit region/locale must agree with an explicit snapshot.
Current pointers are independent per region/locale/version. To browse a different
version, obtain its ID from `/catalogs` and pass `snapshot`. Pin that ID while
paginating; refreshing retains previous snapshots.

Lists accept `prefix`, `offset` (default 0), and `limit` (default 100, capped at
1000). Offset must be between 0 and 10000000; limit must be nonnegative.
`/assets` also accepts `resource_type` and `bundle` (bundle ID).
`/bundles/{id}/equivalents` accepts `limit`, not offset.

```text
GET /assets?region=tw&locale=en&prefix=Live%2FMusicScore%2F&limit=20
GET /bundles?region=tw&locale=zh-Hant&limit=20
GET /bundles/BUNDLE_ID/assets?snapshot=SNAPSHOT_ID
```

BundlePage: `{snapshot, offset, limit, total, bundles: [...]}`.
AssetPage: `{snapshot, offset, limit, total, assets: [...]}`.
Assets expose `key`, `resource_type`, `internal`, `ambiguous`. Ambiguous labels
remain visible; they are not silently discarded or guaranteed exportable.
Bundle membership includes dependency relationships, not just directly owned files.

Bundles expose `id`, stable `key`, `bundle_name`, `internal`, `provider`,
`resource_type`, `catalog_hash`, `crc`, `bytes`, `candidate_id`, `remote`,
`download_sha256`, `plain_sha256`. The last two are null before observation.
`remote=false` dependencies need local game files and cannot be downloaded here.
Catalog statistics expose `snapshot`, `region`, `locale`, `version`, `created`,
`current`, `content_sha256`, `bundles`, `assets`, `declared_bytes`,
`remote_bundles`, `verified_bundles`. Inventory is not a full remote availability check.

## Diffs and verification

`/diffs` accepts `prefix`, `offset`, `limit`, `include_unchanged` (default false).
Response: `{from, to, kind, summary, offset, limit, entries}`. Kind is `region`,
`locale`, or `version`, based on scope differences. Each entry contains `key`,
`change`, `evidence`, `from_id`, `to_id`, `from_count`, `to_count`.
Summary counts all matching stable-key groups before pagination or exclusion of
unchanged entries: `added`, `removed`, `changed`, `unchanged`, `ambiguous`, `unknown`.
Multiple descriptors sharing a key are ambiguous; representative IDs must not be
interpreted as a unique match. Query bundles by key prefix to inspect candidates.

Keys use the Android-relative path with the known catalog hash suffix removed
from bundle filenames. Hash128 + size + CRC + provider family supplies candidate
identity. This is metadata evidence, not proof that bytes match or a claim that
Hash128 is a raw-file MD5. Zero/empty catalog hashes cannot establish identity.
When both plaintext SHA256 observations exist they override candidate metadata.
Diff evidence is `verified_plain_sha256`, `catalog_metadata`, or `inventory`.
Equivalents return snapshot/region/locale/id/key/evidence; evidence is
`catalog_candidate`, `verified_plain_sha256`, or `conflicting_plain_sha256`.

```json
{"ids":["BUNDLE_ID"],"snapshot":"SNAPSHOT_ID"}
```

Send this body to `/bundles/verify` (1–1000 IDs). Alternatively select with
`region` and `locale`. Verification downloads and decrypts sources, records raw
and plaintext SHA256, then releases temporary files. It does not decode every
asset or keep a permanent raw-bundle cache. Filename-dependent encryption can
make raw hashes unsuitable for identifying equal plaintext across sources.

## Exports and tasks

`POST /exports` takes exactly one of nonempty `keys` or a `prefix` string, plus
optional `snapshot`, `region`, `locale`. Unknown JSON members are rejected.
An empty prefix selects the entire catalog. Prefix selections collapse aliases
for an exact target into one export, preferring readable addresses; the browsing
index still retains every alias. Known unsupported types and ambiguous labels
produce results with `skip_reason`, not a download attempt. `skipped` on the task
counts those entries. Skips have null export_id/error and are not exported files.
Completed includes successful, skipped and failed items. A succeeded task can
contain skips; inspect skipped/results when checking export coverage. Missing keys
and actual download/decoder errors still fail individually.

```json
{"keys":["Live/MusicScore/0001/0001_00"],"region":"tw","locale":"en"}
```

POST work requests return the task and a Location header. Tasks expose `id`,
`kind`, `state`, `snapshot`, `total`, `completed`, `results`, `error`, `created`,
`updated`. Times are Unix seconds. States: queued, running, succeeded, partial,
failed, cancelled. Export results contain `key`, `export_id`, `error`.
Cancellation retains already published outputs. Restart fails unfinished tasks;
there is no automatic retry. Operational failures appear in task results.

Manifests contain `id`, `snapshot`, `region`, `key`, `profile`, `sources`, `files`.
Sources include `location`, `download_sha256`, `plain_sha256`. Files include
`id`, `name`, `label`, `media_type`, `bytes`, `sha256`, `metadata`. Labels preserve
source names; names are generated. Metadata includes image/cue/media information.
Separate snapshot manifests can point to the same content-addressed file bytes.
Each request has its own task; identical snapshot/key/profile exports are reused.
Profile: `csharp-json-png-webp-aac-h264-v3`.

Missing records return 404; exhausted queue 429; invalid requests 400. Body limit
is 2 MiB. Files support 206/304/416; mismatched If-Range sends the full file.
Missing physical files return 404 before conditional processing. Successful files
have immutable cache headers. GET never enqueues downloads or reparses catalogs.
No deletion or automatic history/export eviction endpoints are provided.

`/storage` reports snapshots, unique_catalogs, indexed_bundle_rows,
unique_bundle_definitions, unique_asset_definitions, sqlite_main_bytes,
logical_output_bytes, referenced_output_bytes, deduplicated_output_bytes,
reserved_temp_bytes. SQLite main bytes exclude WAL/SHM; output counters exclude
catalogs and metadata. Temporary bytes are reservations, not a disk measurement.

## Sequential all-language queue

Submit once to refresh and export every configured language in order:

```sh
curl -X POST https://YOUR_HOST/tasks/batches \
  -H "Authorization: Bearer $MOENOTES_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"region":"tw"}'
```

The response is 202 with a batch `id` and Location. Defaults: `locales` uses the
region's configured language list, `export=true`, `prefix=""` (all resources).
For catalog-only work, send `{"region":"tw","export":false}`. To limit export
scope, send e.g. `{"region":"tw","locales":["en","ja"],"prefix":"Live/MusicScore/"}`.
The request must be a JSON object; `{}` uses all defaults.

- `GET /tasks/batches?offset=0&limit=100`: summaries in FIFO sequence order,
  including active_task; pagination limit 1–1000.
- `GET /tasks/batches/{id}`: region/version/prefix/state and ordered steps with
  locale, phase, state, task_id, snapshot and error.
- `GET /tasks/{step.task_id}`: resource-level completed/total/results for a child.
- `POST /tasks/batches/{id}/cancel`: cancel pending steps and the active child;
  retain completed exports. Cancellation drains the child before the next batch.

All these routes require the administrative Bearer key. Batch states are queued,
running, succeeded, partial, failed, cancelled. Steps additionally use skipped.
One batch runs at a time; each language refreshes then exports before the next.
A failed refresh skips its export and continues to the next language. Resource
failures remain in child task results. Every configured language is included;
unsupported/local resources can still fail. Full exports may require substantial
network, CPU and output storage despite output deduplication.

The SQLite-backed FIFO survives restarts: completed steps are retained, interrupted
steps are retried, and completed outputs are reused. Export retries use the
snapshot selected by their refresh step. Explicitly cancelled batches do not
resume. This behavior is specific to batch submissions; standalone interrupted
tasks remain failed. Existing standalone endpoints can still run concurrently
within the same worker/download limits. Submit bulk work through this queue to
serialize it. `queue_limit` caps nonterminal batches; excess submissions return
429. Duplicate submissions create separate queued batches.

Zeabur/container logs include batch admission/start/step/end and child task status.
Export progress is checkpointed/logged after every 20 completed resources (at most
once per second), or on a
resource completion after at least 10 seconds since the last update. A single
long-running resource can therefore leave counts unchanged for longer than 10
seconds. Terminal task logs include final completed/total and failure counts.
Logs go to stderr so CLI JSON on stdout remains usable; API keys are never logged.

Image exports include both image/png and lossless image/webp files with the same
label and dimensions, each with its own file ID. The profile version changed so
new requests do not reuse older PNG-only manifests. Existing file URLs remain
valid. WebP encoding uses SkiaSharp in the worker, without an FFmpeg subprocess.

Tasks report `reused`, and item results report boolean `reused`. A manifest's
optional `reused_from` identifies the original conversion. Identical verified
plaintext dependencies, target, codec profile, CRI key and class database can
reuse decoding/transcoding across locales/regions while preserving scoped source
observations and manifests. This is independent of file-byte deduplication. Each
new source scope still downloads and verifies its inputs; catalog hashes alone
never skip downloads. Missing referenced output blobs trigger fresh conversion.

Raising `downloads` does not bypass temporary-space admission. Work waits when
`temp_bytes` reservations are occupied; the configured value is a byte budget,
not a measurement of disk free space or RAM. Existing terminal tasks are historical
records: after fixing a failed deployment, resubmit `/exports` with the same
snapshot and prefix (or failed keys). Already published exports are reused;
terminal failed results are not automatically erased or retried.
