# JP asset retrieval

JP follows the existing metadata poll → release batch → catalog → export pipeline.
Configure the metadata service's `current_version.json`, a JP region with
`metadata_region = "jp"`, and exactly one locale, `ja` (or the empty locale).
The JP catalog has no language suffix. For a JP-only service:

```toml
region = "jp"
locale = "ja"
locales = ["ja"]
cdn_root = "https://static.bang-dream-on.jp"
version_url = "https://metadata.bdon.moe/current_version.json"
```

Run `update CONFIG.toml`, or enable the normal version poller. The first update
exports the selected region in full, as for international regions. A manual
refresh follows the most recently detected release; discover one with `update`
or `/versions/check` before the first JP refresh. A fresh data directory is still
required when coming from the archived Rust service.

The metadata entry's `assets` object describes the Android publication:

```json
{
  "provider": "jp",
  "platform": "Android",
  "version": "1.0.0.100",
  "hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "catalog_url": "https://static.bang-dream-on.jp/asset/1.0.0.100/Android/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/catalog_main.bin",
  "bundle_root": "https://static.bang-dream-on.jp/asset/1.0.0.100/Android/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "api_root": "https://api.bang-dream-on.jp",
  "client_version": "1.0.4"
}
```

These are example version/hash values. They must agree with the containing
entry's `resource_version`, `resource_hash`, `client_version` and `upstream`.
For compatibility with the preceding metadata service, the unpacker can derive
the same paths from those fields when `assets` is absent.

Anonymous HTTP/2 gRPC `MasterdataService/Version` returns `x-sirius-env`,
`x-sirius-cred` and `x-asset-version`. The unpacker selects the greatest applicable
`live.minClientVersion`, validates the result against the fixed catalog
publication, and sends Basic authentication plus `OurNotes/<client_version>`
to the configured JP CDN origin. Live entries with no applicable minimum do not
fall back to the top-level payload. A metadata/native observation mismatch fails
the release; the next metadata poll can queue the new publication.

The API and CDN origins must match `jp_api_origin` and `jp_cdn_origin` (official
defaults). TLS hostname/CA checks remain enabled, proxies and redirects are
disabled, and credentials are attached per CDN request. A cached credential is
reobserved after five minutes, a new publication, restart, or one 401/403. 429 is
not retried. Passwords/Authorization never enter snapshots, batches, releases,
public manifests or configuration. All persisted context is public metadata.

Catalog responses may be gzip compressed; compressed and expanded sizes are
bounded. `{Fwk.Resource.RemoteAssetDir}/` resolves under the pinned bundle root,
with traversal and alternate-host paths rejected. Existing bundle AES-CTR and
CRC/export processing are reused. JP does not request a separate `.hash` file;
the selected Android hash identifies the publication, while downloaded bytes
have their own SHA256. This does not claim that the publication hash is a
cryptographic digest of the catalog response.

JP release IDs and public directories include both version and hash:
`/versions/jp/<version>-<hash>/release.json`. A hash-only update creates a separate
release and snapshot. International release paths and `server.cdnRoot` behavior
remain unchanged.

Validation includes local HTTP/2 and CDN fixtures for anonymous calls, gzip,
placeholder export, credential rotation, 429, stale observations, origin
validation, hash-only updates, restart and persisted-secret absence. On
2026-09-30 JST, the production JP catalog and two small bundle samples were also
retrieved through the actual C# service: one chart and one texture/sprite bundle
exported successfully. This is not a full JP audio/video/Live2D or 8 GB export
acceptance. The research host needed an HTTPS DNS dial workaround with original
SNI/CA verification; no IP override is built into the application.

An independent UnityPy check matched all 3,476 chart bytes. Both 512×512 PNGs
had a maximum one-level difference per RGBA channel against UnityPy decoding;
they are not claimed pixel-identical. Output lengths and SHA256 matched the
published manifests. The C# suite passed 90 tests and Go passed its full race
suite and vet. Pre-existing C# whitespace formatting failures were corrected
separately before publication so the full repository formatting check can pass.
