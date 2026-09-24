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
