# Architecture

The Linux service owns a single SQLite database and export volume. Catalog
snapshots are immutable and active parsing graphs are shared. HTTP validates
selection requests; each task retains its snapshot. Selection is by Addressables
logical key, with exact AssetBundle container-path resolution rather than a
filename search. Texture2D/Sprite aliases are accepted only when their source
and dependency lists agree.

Active export and download registries hold weak entries. Reference-counted leases
keep payloads alive while consumers need them, then remove temporary directories.
One caller's cancellation does not cancel another caller's lease. Resource gates
serialize cancellation/retry overlap around publication. Queue, download, worker,
video and temporary-storage budgets are distinct.

Downloads disable redirects and environment proxy inheritance, remain under the
configured CDN root, enforce declared size, and decrypt the bundle prefix while
streaming. A worker checks UnityFS block CRC when nonzero. CRI's catalog CRC=0 is
not claimed as a successful checksum comparison. Local embedded dependencies
without a remote address fail explicitly.

CRI logical assets with exactly one raw-media dependency can bypass playback-only
ScriptableObject/MonoScript dependencies. The selected raw container is still
validated; ambiguous multi-media wrappers are refused. Embedded waveform references
are decoded, not arbitrary paths inferred from a filename.

Workers run the same executable in a separate process under Linux `prlimit`.
Unity uses bounded file/memory Regions; CRI may materialize waveforms and therefore
also relies on process limits. FFmpeg/ffprobe execute through a small Rust exec
wrapper that sets parent-death signaling. Cancellation kills the worker process
group, and container init handles orphan reaping. These are resource controls,
not a hardened hostile-code sandbox. Add container memory, disk and network policy
for deployment.

Each resource writes to an isolated staging directory. Complete validated outputs
are renamed into the export store on the same filesystem, then committed to SQL.
HTTP exposes only SQL-indexed files. Startup removes unindexed publication
directories and unfinished temporary files, retaining successful exports. A task
may partially succeed; a single resource never advertises partial files.

Raw bundles, CRI containers, decoded WAV and demuxed video are not retained as
managed cache entries. Changing the format profile requires downloading again.
Catalog binaries/metadata are retained for reproducibility. Output SHA256 is
recorded after conversion. SHA256 is not used to claim authenticity of publisher
content without an independent trusted expected digest.

Known alpha boundaries: Android binary v2 catalog only; no login or CDN discovery;
no byte-range download resume, automatic retries, multi-instance coordination,
object storage, browser UI, authentication, export garbage collector, external
AWB discovery, cue runtime, alpha video, or arbitrary Unity object export.
