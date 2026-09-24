# HTTP API v1

Base URL: `http://127.0.0.1:8091`. No authentication. Requests use JSON; application
errors contain an `error` string. This alpha API is experimental. Paths identify
opaque service IDs, never arbitrary local paths or URLs.

| Method and path | Result |
|---|---|
| GET /healthz | Liveness |
| GET /readyz | SQLite/storage readiness and reserved temporary bytes |
| POST /v1/catalogs/refresh | 202, catalog-refresh task |
| GET /v1/catalogs | Retained snapshots and current flag |
| GET /v1/assets | Logical keys and types |
| POST /v1/exports | 202, export task; completed work is reused |
| GET /v1/tasks/{id} | Task state and per-resource results |
| POST /v1/tasks/{id}/cancel | Request cancellation, safe for completed tasks |
| GET /v1/exports/{id} | Published manifest |
| GET or HEAD /v1/files/{id} | Immutable file, Range and ETag supported |

`GET /v1/assets` accepts `snapshot`, `prefix`, `resource_type`, `offset` (default
0) and `limit` (default 100, capped at 1000). Supply a snapshot while paginating
to avoid switching versions. Aliases sharing the same source are resolved to one
logical asset; genuinely ambiguous keys are not offered as exportable entries.

`POST /v1/exports` accepts exactly one of a nonempty `keys` array or a `prefix`
string, plus an optional `snapshot`. The empty prefix explicitly selects every
catalog key, including unsupported resource classes; it is not the recommended
way to export only supported assets. Use known prefixes. Keys are sorted and
deduplicated. Nonexistent/unsupported keys produce resource-level failures.

```json
{"snapshot":"OPTIONAL_SNAPSHOT_ID","keys":["Live/MusicScore/0007/0007_03"]}
```

Tasks expose `id`, `kind`, `state`, `snapshot`, `total`, `completed`, `results`,
`error`, `created`, and `updated` (Unix seconds). Progress is checkpointed every
20 completed resources and at task termination, rather than for every byte.
States are `queued`, `running`,
`succeeded`, `partial`, `failed`, or `cancelled`. Result entries have `key`,
`export_id` and `error`. On cancellation, unstarted keys may be absent; completed
published exports remain available. On restart, queued/running tasks become
failed with an interruption message. There is no automatic retry loop.

Manifests contain `id`, `snapshot`, `key`, `profile`, `sources` and `files`. Sources
record downloaded InternalIds, provider/options, and original download SHA256.
Each file has
`id`, `name` (service-generated basename), `label` (original name), `media_type`,
`bytes`, `sha256` and format-specific `metadata`. Labels are not unique; use IDs.
Metadata may include dimensions, cue references, and ffprobe output.

Equivalent export requests receive different task IDs but share active work and
the same published export identity. GET never initiates downloads. Source identity
includes region/CDN/version/locale/catalog SHA256; export identity also includes
the format profile. Catalog remote hash is an update hint, not an independently
verified cryptographic checksum.

Missing records return 404, exhausted task queue returns 429, and invalid requests
return 400. HTTP parsing/body-limit failures use Axum's corresponding 4xx status.
Operational export failures appear in task results, not as an empty success.
File responses may return 206, 304 or 416. Mismatched If-Range falls back to a full
response. Missing physical files return 404 even with a matching conditional
header; error responses are not immutable cache entries. There is no
DELETE/automatic eviction endpoint in this alpha.
