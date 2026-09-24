# C# architecture

The .NET 10 executable has server, CLI and private worker modes. ASP.NET Core
handles HTTP; Microsoft.Data.Sqlite persists snapshots, tasks, manifests and file
records. CLI operations use the same service and wait for task completion.

`Catalog` reads bounded Addressables binary-v2 graphs. `Config` parses strict TOML
and confines asset URLs to the configured HTTPS CDN root. Redirects, proxy
inheritance and automatic decompression are disabled. Downloads enforce declared
sizes, record original SHA256, and apply AES-CTR to the first 16 KiB when required.

`SharedWork` retains producer results until the last consumer releases a lease.
Only the last cancellation cancels a producer. Export publication gates serialize
cancel/retry overlap. Separate queue/download/worker/video gates and conservative
temporary-space reservations prevent unbounded admission. Raw downloads are
removed after their final consumer. Publication gates use weak references.

`Worker` runs as a separate .NET process. AssetsTools.NET parses UnityFS and
serialized objects; uncompressed block data is checked against nonzero catalog
CRC. Exact AssetBundle container paths resolve exported objects. Texture data is
resolved only from loaded bundles. TextAsset bytes bypass string decoding so gzip
and arbitrary binary content survive intact. PNG serialization uses .NET zlib.

Sprites resolve atlas render data when present, combine split alpha, crop and
undo packing rotation, and mask supported tight geometry. Vertex formats and
rasterization work are bounded. The bundled class package supports stripped type
trees; `class_data` can explicitly override it for other Unity revisions.

`CriTables` reads bounded @UTF/AFS2 structures and resolves embedded cue waveform
references. VGAudio decodes HCA after CRC and strict frame/key validation. USM
chunks are demasked, checked for stream ambiguity/end markers, and passed through
FFmpeg. Encoded outputs are probed, compared to their source tracks and fully
decoded before publication. Filenames from source media are never followed.
The pinned VGAudio adapter binds its internal strict frame validator because its
public decode path does not report every invalid frame; changing that dependency
requires rerunning wrong-key and damaged-frame tests. NativeAOT/trimming is not a
supported publication mode.

Worker cancellation kills the process tree. A watchdog samples resident memory,
output/staging sizes and parent liveness; the server enforces a wall timeout.
These are bounded-work controls, not a hardened sandbox or hard address-space/CPU
quota. Use OS/container policies for hostile inputs and strict system limits.

Each resource writes into an isolated staging directory. The service rechecks
output sizes/hashes, renames on the same filesystem, then commits file/manifest
records in one SQL transaction. Only indexed files are served. Crash recovery
cleans unindexed publications and temporary files and fails unfinished tasks.
A single data-directory lock excludes concurrent owners.

The C# store uses `csharp.sqlite`; a legacy `index.sqlite` causes startup rejection
before cleanup. Old Rust data is not imported. Retain it separately and use a new
volume. The original Rust code is retained only as a migration reference; the
solution, CI, Dockerfile and current docs all target C#.

Known boundaries: Android catalog v2; no game login/discovery, arbitrary Unity
object export, external AWB resolution, CPK, multichannel/alpha/multi-track media,
byte-range download resume, automatic retry, multi-instance ownership, remote
object storage, authentication, or export eviction. Synthetic tests run without
publisher resources; real game revisions require separate acceptance testing.

## Relational catalog index and content-addressed outputs

Derived index schema 3 separates snapshot scope from catalog content. Equal full
catalog SHA256 values share a graph; differing catalogs share exact bundle and
asset descriptor definitions through integer memberships. Indexed forward/reverse
edges support cycle-safe recursive dependency queries. Scope-specific observations
record original and decrypted SHA256, never extrapolating observations to another
region. Stable-path SQL diffs report both status and evidence strength.

Older C# derived indexes rebuild from retained catalog binaries without discarding
exports/tasks. Catalog binaries are stored by content SHA256, with legacy snapshot
filename fallback. The browser does not require reparsing a binary on each request.

Publication validates worker outputs and moves each into `blobs/<prefix>/<sha256>`.
Per-export manifests and per-file records preserve scope identities. SQLite commit
makes the publication visible; startup removes orphan blobs after acquiring the
exclusive store lock. Old C# export file paths remain readable. Catalog history
and referenced blobs have no eviction policy. Cross-snapshot output deduplication
saves storage but does not eliminate first downloads or repeated worker decoding.
