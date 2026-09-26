# Model site

The service publishes the Live2D models of the catalog for the
[ournotes-player](https://github.com/empty-sekai/ournotes-player) Live2D viewer (`<ournotes-live2d>`), next to the
charts of the [chart site](CHART_SITE.md) and in the layout of nnnotes `web --live2d` (site format 2):

```text
/chart-site/models.json                 model index: id, manifest path, key, group, canvas, sizes, character names
/chart-site/models/<id>.json            model manifest: logical path -> {asset, size} | {size, parts}
/chart-site/assets/<sha256>.<ext>       content-addressed files, shared with the charts
```

A model is a catalog key `Character/Live2D/<group>/<name>/model/<name>`; its id is `<name>`
(`adv_live2d_rana_003_casual_spring_01`). Caching and CORS are those of the chart site: assets
`public,max-age=31536000,immutable`, manifests and `models.json` `public,max-age=60`.

## What a model holds

Exactly the files the viewer reads (see the player's `docs/data-format.md`):

| File | Source |
|---|---|
| `model.json` | index: moc3, prefab, the drawables' atlas pages, canvas, shader index, the Cubism mask materials |
| `<name>.moc3` | `CubismMoc._bytes` of the model bundle |
| `<name>.prefab.json` | the model prefab as a node list: every GameObject with every component; the fade motion list, the expression list and every AnimationClip (Mecanim clip data, bindings resolved from their CRC32 to paths and fields) inlined; the moc3 canvas |
| `textures/<page>-<hash>.png` | the atlas pages the drawables' `CubismRenderer._mainTexture` name |
| `shaders/shaders.json`, `shaders/…` | "Live2D Cubism/Lit-URP-ADV-optimize" (from the model's closure) and, when a drawable is masked, "Live2D Cubism/Mask": parsed forms and the GLES3 programs the drawables' material keywords select |

The builder (`Live2DModel.cs`, worker mode `live2d`) is a port of nnnotes' `export.Exporter` (the parts a model uses),
`live2d.export_runtime`, `webmodel.export_model` and `shader.py`. JSON is written as nnnotes writes it (Python's
`json.dumps` layout and float repr, `1e999` for infinity), and large JSON objects are stored per top-level key, so a
model's JSON assets, moc3 and GLSL are the same files nnnotes stores.

## The APK

Two things a model needs are not on the CDN; they come from the game's `base.apk`:

- **Script classes.** Every MonoBehaviour of a model bundle names its script in the APK's
  `assets/aa/Android/shared_monoscripts.bundle` (a local dependency of every model key). Components are identified by
  that class (`CubismRenderer`, `Live2DCharacter`, …).
- **Cubism mask materials.** `model.json` `resources` holds the materials the mask pass draws with, the Resources
  `Live2D/Cubism/Materials/Mask` and `MaskCulling` of the boot data (`assets/bin/Data/data.unity3d`), and their shader
  "Live2D Cubism/Mask" (read with the embedded class database: the boot data has no type trees).

These are extracted once per APK (`ApkData.cs`) into `data_dir/apk-data/<sha256 of base.apk>/` (`apk.json` and the mask
shader's files); other versions there are removed. The APK comes from:

- `apk = "/path/to/base.apk"`: a local file; or
- `playfetch = "playfetch"`: [playfetch](https://github.com/Exmeaning/playfetch) pulls `apk_package` (default
  `com.bilibili.sirius`) from Google Play into `data_dir/apk/<package>/<versionCode>/` at the start of every model build
  (`playfetch pull <package> -out-root <data_dir>/apk -mode split`, plus `playfetch_args`). Files already present and
  matching Play's digests are not fetched again, so a build only downloads the APK when the game has a new version;
  older version directories are removed. When a pull fails, the newest APK pulled before is used. playfetch's account
  store and session cache follow its own settings (`PLAYFETCH_CREDENTIALS`, the user config and cache directories;
  the container sets `XDG_CONFIG_HOME=/data/.config` and `XDG_CACHE_HOME=/data/.cache`); without an account it uses its
  anonymous token dispenser.

## Configuration and use

```toml
playfetch = "playfetch"                           # or: apk = "/srv/apk/base.apk"
# apk_package = "com.bilibili.sirius"
# playfetch_args = ["-account", "tw"]
master_root = "https://metadata.bdon.moe/master"  # optional: character names
```

```sh
moenotes-assets model-site config.toml                            # build every model the site lacks or whose inputs changed
moenotes-assets model-site config.toml adv_live2d_rana_003_casual_spring_01
moenotes-assets model-site config.toml --force                    # rebuild every model
moenotes-assets apk-data base.apk apk-data/                       # extract and print the APK data (debugging aids:)
moenotes-assets live2d-model apk-data/ out/ KEY INTERNAL_ID BUNDLE...   # one model's files from local decrypted bundles
curl -X POST http://127.0.0.1:8091/model-site/build -H "Authorization: Bearer $MOENOTES_API_KEY" \
  -H 'Content-Type: application/json' -d '{"models":["adv_live2d_rana_003_casual_spring_01"]}'
```

The task (`kind: model_site`) reports one result per model id and uses the default region's catalog (like the chart
site; the regions serve the same catalog for a language). Models are built by `workers` worker processes. The chart site
and the model site share one directory: their builds run one at a time, and each ends by rewriting `charts.json` and
`models.json` and removing the assets no chart or model references.

**Names.** With `master_root`, a model that `MasterCharacterCostume` maps to one character (its `_live2dPath` is the key
after `Character/Live2D/`) gets, in its manifest's `model` and its `models.json` entry, `character` (the
`MasterCharacter` id), `names` (`{language: the character's name}` from `MasterText`, the languages with a text) and
`label` (the name in the snapshot's language), as nnnotes writes them. Other models (live models, NPCs) are listed by id
and group. Names of models already built are refreshed on every build.

## Stale models and automatic builds

Every model records `builder: "moenotes-assets"` and `inputs`: a hash of the build version (`ModelSite.BuildVersion`),
the APK (its sha256), the key and internal id, and the bundles of the key's closure (their catalog file names, which
carry the content hash, with size and CRC). A build compares these from the catalog alone, before downloading anything,
and builds the models the site lacks and those whose inputs differ: a new costume, a changed model bundle or a new APK.

With version tracking (`version_url`, see [API](API.md)) the model site follows the chart site: a build starts after a
release of the default region whose default language succeeded or was partial, and after a master data change at an
unchanged resource version (names). The server (`serve`) also requests one build when it starts, so a new deployment
builds the models its site lacks without waiting for a release (a server without a catalog snapshot yet logs that the
build could not start; the first release builds it). Requests that arrive during a build are coalesced into one more
build. Logs show
`[model-site] automatic build ID after …`; the CLI `update` waits for it and prints it as `model_site`.

## Verification

Against nnnotes `web --all-live2d` (MetaSekaiLab/nnnotes `f2594f7`) on the TW catalog and the Google Play APK 1.0.1
(versionCode 25): all 239 models build, and every file of every model is the same bytes as nnnotes' (model.json, the
split prefab, moc3, shader parsed forms, index and GLSL programs) except the PNGs, whose pixels differ by at most 1 in a
channel (ASTC decoder rounding, as for chart jackets). The player's `validate-data.mjs` passes the site.

Limits: models with SerializeReference fields, renderer material clip bindings or references into Unity's default
resources are rejected with an error naming the feature (none of the current models has them). Models a newer catalog no
longer lists are kept.
