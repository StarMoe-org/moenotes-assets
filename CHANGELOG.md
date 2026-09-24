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
