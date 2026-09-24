# C# third-party components

Project code is MIT licensed. This does not relicense dependencies or game assets.

- AssetsTools.NET 3.0.5 and AssetsTools.NET.Texture 3.0.2: MIT,
  https://github.com/nesrak1/AssetsTools.NET. Texture decoding uses its transitive
  AssetRipper.TextureDecoder dependency. Exact versions are in packages.lock.json.
- VGAudio-fork 2.3.0: MIT, https://github.com/logicallyanime/VGAudio,
  package repository commit `7ce5c5a9eb96889977cb84c5cd8833d609644702`.
- Tomlyn: BSD-2-Clause. Microsoft.Data.Sqlite: MIT; bundled SQLite has its own
  public-domain/license notices. Direct/transitive versions are locked in
  `CSharp/packages.lock.json`; test dependencies have a separate lock file.
- CRI USM mask derivation and ACB/USM layout handling were ported/adapted with
  reference to cridecoder 0.3.5, MIT, copyright 2026 Haruki Dev Team.
  Its notice is `third_party/cridecoder-csharp-port-LICENSE.txt`.
- Sprite geometry/layout handling was informed by AssetStudio's SpriteHelper,
  MIT, https://github.com/Perfare/AssetStudio. Its notice is
  `third_party/AssetStudio-LICENSE.txt`.
- Embedded `CSharp/Resources/classdata.tpk` is from UABEA `ReleaseFiles/classdata.tpk`,
  https://github.com/nesrak1/UABEA, commit
  `057e2f6ae67ebc94a38faca9f946f8577162fa99`.
  SHA256: `129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6`.
  The upstream distribution license is retained at `third_party/UABEA-LICENSE.txt`.
- FFmpeg/libx264 run as separate executables. The Ubuntu container packages
  include GPL-enabled components; the complete image is not MIT-only. Runtime
  package notices remain in `/usr/share/doc`. Redistribution must satisfy the
  applicable source-distribution and license obligations.

The retained Rust reference implementation still has its original dependency
notices in `third_party/licenses` and version inventory in `Cargo.lock`. Those
crates are not compiled into the C# runtime. No proprietary CRI plugin, game
resource or player credential is included. No public image release is configured.
