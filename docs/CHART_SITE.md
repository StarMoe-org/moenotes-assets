# Chart site

The service can publish an [ournotes-player](https://github.com/StarMoe-org/ournotes-player) chart site: the data a
browser needs to play a chart in 3D. The layout is that of nnnotes `web` (site format 2):

```text
/chart-site/charts.json                          chart index
/chart-site/charts/<musicId>_<difficulty>.json   chart manifest: logical path -> {asset, size} | {size, parts}
/chart-site/assets/<sha256>.<ext>                content-addressed files shared by every chart
```

`musicId` is the MasterLiveMusic id. Assets are served with `Cache-Control: public,max-age=31536000,immutable`;
manifests and `charts.json` with `public,max-age=60`. CORS follows `cors_origins`: add the pages that embed the
player (or `*`).

## Static base and song data

Most of a chart does not depend on the song: the live scene (Unity node lists), lane, note skins, effects, particle
systems, shaders, sound effects and their CRI routing. Much of it lives in the game APK (built-in bundles, boot data,
the ACF), which this service does not read, so it comes from an nnnotes `web` build as a **static package**:

```text
base.json                package facts (format 1, site format 2, templates, stage bands)
templates/<id>.json      the nnnotes chart manifests, used as templates
assets/<sha256>.<ext>    every file they reference except the songs' own notes, BGM and jacket
```

`moenotes-assets chart-base <nnnotes site> <out.zip> <source>` packs one (deterministic zip; it prints the sha256).
The TW 1.0.1 package is the release `static-base-tw-1.0.1` of ournotes-player (25.7 MB, 52 MB extracted, 336 templates).
With `chart_base_url` the service downloads it on the first `chart-site` run (redirects followed, 30 min limit),
checks `chart_base_sha256`, extracts it to `data_dir/chart-base/<sha256>/` (entry names and every asset's hash
checked) and removes other versions there, so a new server needs nothing but its configuration. `chart_base` may
instead name a local directory: a package, or a whole nnnotes `web` site, whose charts are then published as they
are (base charts replace charts built here). Template files are read in the base directory and hard-linked (or
copied) into `data_dir/chart-site/` as charts use them.

Every chart the site lacks (with a package: every chart) is composed here from the song's own exports in the current
snapshot:

| File | Source |
|---|---|
| `score/<file>.notes.json` | `Live/MusicScore/<file>` converted to runtime notes (`ChartScore`, a port of nnnotes `score.convert`) |
| `audio/<sheet>/<cue>.m4a` | `Cri/Sound/<sheet>` exported as usual (AAC); its encoder delay is read from the MP4 edit list |
| BGM entry of `audio/live-audio.json` | the sheet's ACB (worker mode `acb`, nothing published) parsed like nnnotes `acb_cues`: categories, volume, bus sends, waveform length |
| jacket in `livescene/scene.json` | `Image/Jacket/<jacket>` PNG, as `sprites.jacket.texture` |
| `live.json`, manifest facts | MasterLiveMusic / MasterLiveMusicScore / MasterSound / MasterText rows (`master_root`) |

Everything else comes from the templates of the song's stage band (the band of its first vocal character, as
nnnotes chooses by default): the template is the band's chart with the most files; the static files are those
of every chart of the band, and their `shaders.json` indexes (each filtered by nnnotes to what its chart reads)
are merged, so no note type of the new chart lacks a file. Only the changed parts of the split `scene.json`
(`sprites`, `slice`, `master`) are stored again; the shared stage parts keep their assets. Built manifests carry
`builder: "moenotes-assets"` and `template: <template id>`.

## Configuration and use

```toml
chart_base_url = "https://github.com/StarMoe-org/ournotes-player/releases/download/static-base-tw-1.0.1/ournotes-static-base-tw-1.0.1.zip"
chart_base_sha256 = "ab6ee0103e32812be74e61735a5479b6403ceb624d96613d0e70dbd17b3cca7a"
master_root = "https://metadata.bdon.moe/master"   # decoded MasterData: <root>/<Table>.json with _allData rows
# or, instead of the URL: chart_base = "/srv/static-base"  (a package directory or an nnnotes `web` site)
```

```sh
moenotes-assets chart-site config.toml                 # fetch the base if needed, build every missing chart
moenotes-assets chart-site config.toml 100042 100043   # only these musics
moenotes-assets chart-site config.toml --force 100042  # rebuild them
curl -X POST http://127.0.0.1:8091/chart-site/build -H "Authorization: Bearer $MOENOTES_API_KEY" \
  -H 'Content-Type: application/json' -d '{"music":[100042]}'
```

The task (`kind: chart_site`) reports one result per chart id. `charts.json` is rewritten at the end and assets no
manifest references are removed. A new game version that changes the stage or notes needs a new package (a new
release and sha256 in the configuration); new songs do not.

## Stale charts and automatic builds

Every chart built here records `inputs` in its manifest: a hash of the build version (`ChartSite.BuildVersion`), the
static base (`chart_base_sha256`, or the package's `base.json`), the locale, the song's master rows (MasterLiveMusic,
MasterLiveMusicScore, MasterSound, texts, stage band) and the sha256 of its published score, BGM files, the BGM cue
sheet's plaintext source (which holds the ACB) and jacket. A build first computes these from export manifests and
master data only, then composes the charts the site lacks and those whose recorded `inputs` differ: a new score,
BGM, jacket, level or title rebuilds just the affected charts. Charts built before `inputs` were recorded are
rebuilt once. Charts taken from an nnnotes site are never rebuilt; `--force` still rebuilds every built chart.

With version tracking (`version_url`, see [API](API.md)) and the chart site configured, builds start on their own:
after a release of the default region whose default language succeeded or was partial, and when that region's
master data version changes at an unchanged resource version (songs unlocked by master rows alone). Requests that
arrive during a build are coalesced into one more build. Logs show `[chart-site] automatic build ID after …`; the CLI
`update` waits for these builds and prints the last one as `chart_site`.

## Verification

- `ChartScoreTests.MatchesTheReferenceOnRealCharts` compares the converter with nnnotes `score.convert` output when
  `MOENOTES_CHART_ORACLE=<dir>` holds `raw/<name>.json` charts and `expected/<name>.json` references. All 336 charts
  of TW 1.0.1 match field for field.
- `ChartSiteTests.ReadsCueSheetsLikeTheReference` compares the ACB parser with nnnotes `acb_cues` when
  `MOENOTES_NNNOTES=<nnnotes>/src` is set and Python is on `PATH`.
- Against the TW 1.0.1 base (nnnotes `bcd7f20`, 336 charts; the player's `validate-data.mjs` passes all 336): the
  converter equals every chart's `notes.json`. With the newest song of each stage band left out of the base
  (100098, 100105, 100106, 100107, 100110; 20 charts), the charts built here from this service's exports equal the
  base's in facts, notes, sound definitions (BGM row, categories, volume, bus sends, samples, encoder delay) and
  scene, apart from the jacket texture's file name and ±1 ASTC decoder rounding in its pixels; they carry up to two
  more static files of their band and a superset shader index. They load and play in the browser.

Limits: BGM loop points are not exported (the player only loops waveforms with `loopFlag` 2). A band without a base
chart falls back to any base chart's stage. Sheets whose cues are not sequences of one-waveform synths are rejected,
as in nnnotes.
