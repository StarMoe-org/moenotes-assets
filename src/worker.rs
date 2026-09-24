use crate::{
    catalog::{CRI, Location},
    config::Config,
    crypto,
};
use anyhow::{Context, Result, bail, ensure};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    collections::BTreeSet,
    fs::File,
    io::{Cursor, Read, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
use unity_rs_core::{
    image_export::{ImageFormat, ImageRowOrder, write_rgba_image},
    loader::AssetLoadOptions,
    serialized::ContainerMetadataReadLimits,
    source::Region,
    sprite::SpriteReadLimits,
    studio::{Studio, StudioObject},
    texture::TextureReadLimits,
};

pub const PROFILE: &str = "json-png-aac-h264-v1";
#[derive(Clone, Serialize, Deserialize)]
pub struct Input {
    pub location: Location,
    pub path: PathBuf,
}
#[derive(Serialize, Deserialize)]
pub struct Job {
    pub config: Config,
    pub target: Location,
    pub inputs: Vec<Input>,
    pub output: PathBuf,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Artifact {
    pub name: String,
    pub label: String,
    pub media_type: String,
    pub bytes: u64,
    pub sha256: String,
    pub metadata: Value,
}
#[derive(Serialize, Deserialize)]
pub struct WorkerResult {
    pub files: Vec<Artifact>,
    pub error: Option<String>,
}

struct Output<'a> {
    job: &'a Job,
    files: Vec<Artifact>,
    total: u64,
}
impl Output<'_> {
    fn path(&self, ext: &str) -> PathBuf {
        self.job
            .output
            .join(format!("{:05}.{ext}", self.files.len()))
    }
    fn add(&mut self, path: PathBuf, label: String, media: &str, metadata: Value) -> Result<()> {
        let n = path.metadata()?.len();
        self.total = self.total.checked_add(n).context("output overflow")?;
        ensure!(
            self.total <= self.job.config.output_bytes,
            "output budget exceeded"
        );
        ensure!(self.files.len() < 10_000, "output file count limit");
        use sha2::Digest;
        let mut hasher = sha2::Sha256::new();
        let mut f = File::open(&path)?;
        let mut buf = [0; 65536];
        loop {
            let n = f.read(&mut buf)?;
            if n == 0 {
                break;
            }
            hasher.update(&buf[..n]);
        }
        self.files.push(Artifact {
            name: path
                .file_name()
                .context("output basename")?
                .to_string_lossy()
                .into(),
            label,
            media_type: media.into(),
            bytes: n,
            sha256: hex::encode(hasher.finalize()),
            metadata,
        });
        Ok(())
    }
    fn bytes(&mut self, data: &[u8], ext: &str, label: String, media: &str) -> Result<()> {
        ensure!(
            data.len() as u64 <= self.job.config.output_bytes.saturating_sub(self.total),
            "output budget"
        );
        let p = self.path(ext);
        File::create(&p)?.write_all(data)?;
        self.add(p, label, media, Value::Null)
    }
}

pub fn run(job: &Job) -> Result<Vec<Artifact>> {
    job.config.validate()?;
    ensure!(!job.inputs.is_empty(), "empty worker inputs");
    std::fs::create_dir(&job.output)?;
    let mut output = Output {
        job,
        files: vec![],
        total: 0,
    };
    let media = job.target.provider == CRI || job.target.resource_type.starts_with("CriWare.");
    if media {
        let raw: Vec<_> = job
            .inputs
            .iter()
            .filter(|i| i.location.provider == CRI)
            .collect();
        ensure!(raw.len() == 1, "unsupported or ambiguous CRI dependencies");
        cri(&raw[0].path, &mut output)?;
    } else {
        let mut regions = vec![];
        let mut sum = 0;
        let mut inputs: Vec<_> = job.inputs.iter().collect();
        inputs.sort_by_key(|i| i.location.id);
        ensure!(
            inputs
                .windows(2)
                .all(|w| w[0].location.id != w[1].location.id),
            "duplicate dependency ID"
        );
        for i in inputs {
            ensure!(
                i.location.provider != CRI,
                "unsupported mixed provider target"
            );
            let size = i.path.metadata()?.len();
            sum += size;
            ensure!(sum <= job.config.expanded_bytes, "input set budget");
            let crc = crypto::bundle_crc(&i.path, job.config.expanded_bytes)?;
            if let Some(options) = &i.location.options {
                ensure!(
                    options.crc == 0 || crc == options.crc,
                    "bundle CRC mismatch"
                );
            }
            let region =
                if size <= job.config.memory_threshold && sum <= job.config.memory_threshold {
                    Region::from_bytes(std::fs::read(&i.path)?)
                } else {
                    Region::from_file(&i.path)?
                };
            regions.push((format!("dependency-{}.bundle", i.location.id), region));
        }
        let mut options = AssetLoadOptions::default();
        options.limits.maximum_expanded_bytes = job.config.expanded_bytes;
        options.limits.maximum_single_entry_bytes = job.config.expanded_bytes.min(512 << 20);
        options.limits.maximum_discovered_files = 10_000;
        options.limits.maximum_object_metadata_entries = 1_000_000;
        let studio = Studio::open_regions_with_options(regions, options)?;
        let mut targets = BTreeSet::new();
        for object in studio.objects().filter(|v| v.class_id() == 142) {
            let metadata = object.read_asset_bundle(ContainerMetadataReadLimits::default())?;
            for entry in metadata.container {
                if entry.key == job.target.internal {
                    let resolved = unity_rs_core::scene::resolve_object_reference(
                        studio.collection(),
                        object.file_index(),
                        entry.asset,
                    )?
                    .context("null asset pointer")?;
                    targets.insert((resolved.file_index, resolved.object.path_id));
                }
            }
        }
        ensure!(
            !targets.is_empty(),
            "target InternalId not found in bundle containers"
        );
        for (file, id) in targets {
            let object = studio.object(file, id).context("resolved object missing")?;
            if object.class_id() == 687078895 {
                let atlas = object.read_sprite_atlas(Default::default())?;
                for reference in atlas.packed_sprites {
                    let resolved = unity_rs_core::scene::resolve_object_reference(
                        studio.collection(),
                        file,
                        reference,
                    )?
                    .context("null atlas sprite")?;
                    unity_object(
                        studio
                            .object(resolved.file_index, resolved.object.path_id)
                            .context("atlas sprite missing")?,
                        &mut output,
                    )?;
                }
            } else {
                unity_object(object, &mut output)?;
            }
        }
    }
    ensure!(!output.files.is_empty(), "no supported outputs");
    Ok(output.files)
}

fn unity_object(object: StudioObject<'_>, out: &mut Output<'_>) -> Result<()> {
    let label = object.name().unwrap_or("unnamed").to_owned();
    match object.class_id() {
        49 => {
            let raw =
                object.read_text_bytes(out.job.config.expanded_bytes.min(64 << 20) as usize)?;
            let data = decode_text(raw, out.job.config.expanded_bytes.min(64 << 20))?;
            let (ext, mime) = if serde_json::from_slice::<Value>(&data).is_ok() {
                ("json", "application/json")
            } else if data.starts_with(b"#TITLE ") {
                ("sus", "text/plain; charset=utf-8")
            } else if std::str::from_utf8(&data).is_ok() {
                ("txt", "text/plain; charset=utf-8")
            } else {
                ("bin", "application/octet-stream")
            };
            out.bytes(&data, ext, label, mime)
        }
        28 | 213 => {
            let limits = TextureReadLimits {
                maximum_dimension: 16384,
                maximum_output_bytes: out.job.config.output_bytes.min(512 << 20),
                maximum_decoder_working_bytes: 512 << 20,
                ..Default::default()
            };
            let (image, order) = if object.class_id() == 28 {
                (
                    object.decode_texture_mip(0, limits)?,
                    ImageRowOrder::UnityDecoded,
                )
            } else {
                (
                    object.decode_sprite(SpriteReadLimits::default(), limits)?,
                    ImageRowOrder::Display,
                )
            };
            let p = out.path("png");
            write_rgba_image(
                &image,
                ImageFormat::Png,
                order,
                out.job.config.output_bytes.saturating_sub(out.total),
                &mut File::create(&p)?,
            )?;
            out.add(
                p,
                label,
                "image/png",
                json!({"width":image.width,"height":image.height}),
            )
        }
        id => bail!("unsupported Unity class {id}"),
    }
}

fn command(c: &Config, args: &[String]) -> Result<()> {
    let status = media_command(&c.ffmpeg)?
        .args([
            "-nostdin",
            "-v",
            "error",
            "-xerror",
            "-y",
            "-threads",
            &c.ffmpeg_threads.to_string(),
        ])
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()?;
    ensure!(status.success(), "FFmpeg conversion/validation failed");
    Ok(())
}
pub fn probe(c: &Config, path: &Path) -> Result<Value> {
    let data = media_command(&c.ffprobe)?
        .args([
            "-v",
            "error",
            "-show_streams",
            "-show_format",
            "-of",
            "json",
        ])
        .arg(path)
        .stdin(Stdio::null())
        .stderr(Stdio::null())
        .output()?;
    ensure!(
        data.status.success() && data.stdout.len() <= 1 << 20,
        "ffprobe failed"
    );
    let mut value: Value = serde_json::from_slice(&data.stdout)?;
    if let Some(format) = value.get_mut("format").and_then(Value::as_object_mut) {
        format.remove("filename");
    }
    Ok(value)
}
fn media_command(binary: &str) -> Result<Command> {
    let mut c = Command::new(std::env::current_exe()?);
    c.arg("media-exec").arg(binary);
    Ok(c)
}
fn validate_media(c: &Config, path: &Path, video: bool) -> Result<Value> {
    let p = probe(c, path)?;
    let streams = p["streams"].as_array().context("missing streams")?;
    ensure!(!streams.is_empty(), "empty media");
    let videos = streams
        .iter()
        .filter(|s| s["codec_type"] == "video")
        .count();
    let audios = streams
        .iter()
        .filter(|s| s["codec_type"] == "audio")
        .count();
    ensure!(
        videos == usize::from(video) && if video { audios <= 1 } else { audios == 1 },
        "output track count mismatch"
    );
    for s in streams {
        match s["codec_type"].as_str() {
            Some("video") => ensure!(video && s["codec_name"] == "h264", "wrong video codec"),
            Some("audio") => ensure!(s["codec_name"] == "aac", "wrong audio codec"),
            _ => bail!("unexpected output stream"),
        }
    }
    command(
        c,
        &[
            "-i".into(),
            path.display().to_string(),
            "-map".into(),
            "0".into(),
            "-f".into(),
            "null".into(),
            "-".into(),
        ],
    )?;
    Ok(p)
}
fn wav(raw: Vec<u8>, subkey: u16, c: &Config, path: &Path) -> Result<u32> {
    let mut verifier = cridecoder::hca::ClHca::new();
    verifier.decode_header(&raw)?;
    let header = verifier.get_info()?;
    let effective = if subkey == 0 {
        c.cri_key
    } else {
        c.cri_key
            .wrapping_mul(((subkey as u64) << 16) | ((!subkey as u64) + 2))
    };
    verifier.set_key(effective);
    ensure!(header.block_size > 0, "invalid HCA block size");
    let end =
        (header.header_size as u64) + (header.block_count as u64) * (header.block_size as u64);
    ensure!(end <= raw.len() as u64, "truncated HCA");
    for block in raw[header.header_size as usize..end as usize].chunks(header.block_size as usize) {
        let mut bytes = block.to_vec();
        ensure!(
            verifier.test_block(&mut bytes) >= 0,
            "HCA key or block validation failed"
        );
    }
    let mut decoder = cridecoder::HcaDecoder::from_reader(Cursor::new(raw))?;
    decoder.set_encryption_key(c.cri_key, subkey.into());
    let info = decoder.info();
    ensure!(
        (1..=2).contains(&info.channel_count),
        "unsupported channel count"
    );
    let pcm =
        info.block_count as u64 * info.samples_per_block as u64 * info.channel_count as u64 * 2;
    ensure!(pcm <= c.expanded_bytes, "PCM expansion limit");
    let channels = info.channel_count;
    decoder.decode_to_wav(&mut File::create(path)?)?;
    Ok(channels)
}
fn cri(path: &Path, out: &mut Output<'_>) -> Result<()> {
    let c = &out.job.config;
    let mut f = File::open(path)?;
    let mut magic = [0; 4];
    f.read_exact(&mut magic)?;
    let stage = out.job.output.parent().context("work dir")?;
    match &magic {
        b"@UTF" => {
            let table = cridecoder::acb::UtfTable::new(File::open(path)?)?;
            ensure!(table.rows.len() == 1, "invalid ACB header rows");
            let tracks = cridecoder::acb::TrackList::new(&table)?;
            ensure!(
                tracks.tracks.iter().all(|t| !t.is_stream),
                "external AWB dependency is not supported yet"
            );
            let awb = table.rows[0]
                .get("AwbFile")
                .and_then(|v| v.as_bytes())
                .context("missing embedded AWB")?;
            check_awb_references(awb, &tracks.tracks)?;
            // No caller path: do not let a bank probe arbitrary filesystem companion names.
            let waves = cridecoder::extract_acb_unique_to_memory(File::open(path)?, None)?;
            let expected: BTreeSet<_> = tracks
                .tracks
                .iter()
                .map(|t| (t.is_stream, t.stream_awb_id, t.wav_id))
                .collect();
            ensure!(waves.len() == expected.len(), "missing ACB waveforms");
            ensure!(!waves.is_empty() && waves.len() <= 10_000, "waveform count");
            let bytes: usize = waves.iter().map(|v| v.data.len()).sum();
            ensure!(bytes as u64 <= c.expanded_bytes, "waveform budget");
            for (i, w) in waves.into_iter().enumerate() {
                ensure!(
                    w.extension.eq_ignore_ascii_case("hca"),
                    "unsupported waveform codec"
                );
                let tmp = stage.join(format!("wave-{i}.wav"));
                let channels = wav(w.data, w.subkey, c, &tmp)?;
                let original_probe = probe(c, &tmp)?;
                let p = out.path("m4a");
                command(
                    c,
                    &[
                        "-i".into(),
                        tmp.display().to_string(),
                        "-map_metadata".into(),
                        "-1".into(),
                        "-c:a".into(),
                        "aac".into(),
                        "-b:a".into(),
                        if channels == 1 { "96k" } else { "192k" }.into(),
                        "-movflags".into(),
                        "+faststart".into(),
                        "-fs".into(),
                        c.output_bytes.to_string(),
                        p.display().to_string(),
                    ],
                )?;
                let metadata = validate_media(c, &p, false)?;
                compare_tracks(&original_probe, None, &metadata)?;
                let cues: Vec<_> = w
                    .cues
                    .iter()
                    .map(|v| json!({"name":v.name,"id":v.cue_id}))
                    .collect();
                let label = w
                    .cues
                    .first()
                    .map(|v| v.name.clone())
                    .unwrap_or_else(|| format!("wave-{i}"));
                std::fs::remove_file(tmp)?;
                out.add(p, label, "audio/mp4", json!({"probe":metadata,"cues":cues}))?;
            }
            Ok(())
        }
        b"CRID" => {
            let expected_audio = check_usm_channels(path)?;
            let streams = cridecoder::extract_usm_to_memory(
                File::open(path)?,
                b"movie",
                Some(c.cri_key),
                true,
            )?;
            ensure!(
                streams.len() == 1 + usize::from(expected_audio),
                "missing or extra USM streams"
            );
            let mut video = None;
            let mut audio = None;
            for (i, s) in streams.into_iter().enumerate() {
                ensure!(
                    s.data.len() as u64 <= c.expanded_bytes,
                    "USM expansion limit"
                );
                let ext = if s.data.starts_with(b"DKIF") {
                    "ivf"
                } else if s.extension == "hca" {
                    "hca"
                } else if s.extension == "adx" {
                    "adx"
                } else if s.extension == "m2v" {
                    "m2v"
                } else {
                    bail!("unsupported USM codec")
                };
                let p = stage.join(format!("stream-{i}.{ext}"));
                if ext == "hca" {
                    let p = p.with_extension("wav");
                    wav(s.data, 0, c, &p)?;
                    ensure!(audio.replace(p).is_none(), "multiple audio tracks");
                } else {
                    File::create(&p)?.write_all(&s.data)?;
                    if ext == "adx" {
                        ensure!(audio.replace(p).is_none(), "multiple audio tracks");
                    } else {
                        ensure!(video.replace(p).is_none(), "multiple video tracks");
                    }
                }
            }
            let video = video.context("no video stream")?;
            let original_probe = probe(c, &video)?;
            let mut args = vec!["-i".into(), video.display().to_string()];
            let mut audio_bitrate = "192k";
            let mut audio_probe = None;
            if let Some(a) = &audio {
                let p = probe(c, a)?;
                ensure!(
                    p["streams"][0]["channels"]
                        .as_u64()
                        .is_some_and(|n| (1..=2).contains(&n)),
                    "unsupported channels"
                );
                if p["streams"][0]["channels"] == 1 {
                    audio_bitrate = "96k";
                }
                audio_probe = Some(p);
                args.extend(["-i".into(), a.display().to_string()]);
            }
            args.extend(["-map".into(), "0:v:0".into()]);
            if audio.is_some() {
                args.extend([
                    "-map".into(),
                    "1:a:0".into(),
                    "-c:a".into(),
                    "aac".into(),
                    "-b:a".into(),
                    audio_bitrate.into(),
                ]);
            }
            let p = out.path("mp4");
            args.extend([
                "-map_metadata".into(),
                "-1".into(),
                "-c:v".into(),
                "libx264".into(),
                "-threads".into(),
                c.ffmpeg_threads.to_string(),
                "-crf".into(),
                "20".into(),
                "-preset".into(),
                "medium".into(),
                "-vf".into(),
                "pad=ceil(iw/2)*2:ceil(ih/2)*2".into(),
                "-pix_fmt".into(),
                "yuv420p".into(),
                "-movflags".into(),
                "+faststart".into(),
                "-fs".into(),
                c.output_bytes.to_string(),
                p.display().to_string(),
            ]);
            command(c, &args)?;
            let metadata = validate_media(c, &p, true)?;
            compare_tracks(&original_probe, audio_probe.as_ref(), &metadata)?;
            out.add(p, out.job.target.key.clone(), "video/mp4", metadata)
        }
        _ => bail!("unsupported CRI container"),
    }
}
fn decode_text(raw: Vec<u8>, limit: u64) -> Result<Vec<u8>> {
    if !raw.starts_with(&[0x1f, 0x8b]) {
        ensure!(raw.len() as u64 <= limit, "text expansion limit");
        return Ok(raw);
    }
    let mut data = vec![];
    flate2::read::MultiGzDecoder::new(raw.as_slice())
        .take(limit + 1)
        .read_to_end(&mut data)?;
    ensure!(data.len() as u64 <= limit, "text expansion limit");
    Ok(data)
}

fn compare_tracks(input: &Value, audio: Option<&Value>, output: &Value) -> Result<()> {
    let mut expected = Vec::new();
    for probe in std::iter::once(input).chain(audio) {
        let tracks = probe["streams"]
            .as_array()
            .context("missing source tracks")?;
        ensure!(tracks.len() == 1, "ambiguous source tracks");
        expected.push((probe, &tracks[0]));
    }
    let actual = output["streams"]
        .as_array()
        .context("missing output tracks")?;
    ensure!(
        actual.len() == expected.len(),
        "output track count mismatch"
    );
    for (probe, src) in expected {
        let kind = src["codec_type"].as_str().context("source track type")?;
        let dst = actual
            .iter()
            .find(|s| s["codec_type"] == kind)
            .context("output track missing")?;
        let duration = |p: &Value, s: &Value| {
            s["duration"]
                .as_str()
                .or_else(|| p["format"]["duration"].as_str())
                .and_then(|v| v.parse::<f64>().ok())
                .filter(|n| n.is_finite() && *n >= 0.0)
        };
        ensure!(
            (duration(probe, src).context("source duration unavailable")?
                - duration(output, dst).context("output duration unavailable")?)
            .abs()
                <= 0.1,
            "media duration mismatch or truncated output"
        );
        match kind {
            "audio" => ensure!(
                src["channels"] == dst["channels"] && src["sample_rate"] == dst["sample_rate"],
                "audio format changed"
            ),
            "video" => {
                for dim in ["width", "height"] {
                    let n = src[dim].as_u64().context("video dimension")?;
                    ensure!(
                        dst[dim].as_u64() == Some(n.div_ceil(2) * 2),
                        "video dimension changed"
                    );
                }
            }
            _ => bail!("unsupported source track"),
        }
    }
    Ok(())
}

fn check_awb_references(data: &[u8], tracks: &[cridecoder::acb::Track]) -> Result<()> {
    ensure!(
        data.len() >= 16 && data.starts_with(b"AFS2"),
        "invalid embedded AWB"
    );
    let count = u32::from_le_bytes(data[8..12].try_into()?) as usize;
    let offset_size = data[5] as usize;
    let id_size = data[6] as usize;
    let alignment = u16::from_le_bytes(data[12..14].try_into()?) as usize;
    ensure!(
        count <= 10_000
            && matches!(offset_size, 2 | 4)
            && matches!(id_size, 2 | 4)
            && alignment > 0,
        "unsupported AWB header"
    );
    let table_end = 16 + count * id_size + (count + 1) * offset_size;
    ensure!(table_end <= data.len(), "truncated AWB table");
    let number = |b: &[u8]| {
        b.iter()
            .enumerate()
            .map(|(i, b)| (*b as u32) << (i * 8))
            .sum::<u32>()
    };
    let mut ids = BTreeSet::new();
    for i in 0..count {
        ensure!(
            ids.insert(number(&data[16 + i * id_size..16 + (i + 1) * id_size])),
            "duplicate AWB wave ID"
        );
    }
    let offsets = &data[16 + count * id_size..table_end];
    let mut previous = table_end;
    for i in 0..=count {
        let n = number(&offsets[i * offset_size..(i + 1) * offset_size]) as usize;
        ensure!(n >= previous && n <= data.len(), "invalid AWB offset");
        let start = if i < count {
            n.div_ceil(alignment) * alignment
        } else {
            n
        };
        ensure!(start <= data.len(), "invalid AWB alignment");
        previous = start;
    }
    for t in tracks {
        ensure!(
            t.wav_id >= 0 && ids.contains(&(t.wav_id as u32)),
            "ACB references a missing AWB waveform"
        );
    }
    Ok(())
}
fn check_usm_channels(path: &Path) -> Result<bool> {
    use std::io::{Seek, SeekFrom};
    let mut f = File::open(path)?;
    let size = f.metadata()?.len();
    let mut pos = 0u64;
    let mut videos = BTreeSet::new();
    let mut audios = BTreeSet::new();
    while pos < size {
        let mut h = [0; 32];
        f.read_exact(&mut h)?;
        let n = u32::from_be_bytes(h[4..8].try_into()?) as u64 + 8;
        ensure!(n >= 32 && pos + n <= size, "invalid USM chunk");
        let header = u16::from_be_bytes(h[8..10].try_into()?) as u64;
        let padding = u16::from_be_bytes(h[10..12].try_into()?) as u64;
        ensure!(
            header == 24 && header + padding <= n - 8,
            "unsupported USM header/padding"
        );
        ensure!(
            matches!(&h[..4], b"CRID" | b"@SFV" | b"@SFA"),
            "unsupported USM stream type"
        );
        ensure!(&h[..4] != b"@ALP", "unsupported alpha video");
        if &h[..4] == b"@SFV" {
            videos.insert(h[12]);
        }
        if &h[..4] == b"@SFA" {
            audios.insert(h[12]);
        }
        pos += n;
        f.seek(SeekFrom::Start(pos))?;
    }
    ensure!(
        videos.len() == 1 && audios.len() <= 1,
        "unsupported USM tracks"
    );
    Ok(!audios.is_empty())
}

pub fn worker_entry(path: &Path) -> Result<()> {
    let bytes = std::fs::read(path)?;
    ensure!(bytes.len() <= 16 << 20, "worker request limit");
    let job: Job = serde_json::from_slice(&bytes)?;
    let result = match run(&job) {
        Ok(files) => WorkerResult { files, error: None },
        Err(e) => WorkerResult {
            files: vec![],
            error: Some(format!("{e:#}")),
        },
    };
    File::create(path.with_extension("result.json"))?.write_all(&serde_json::to_vec(&result)?)?;
    Ok(())
}

#[cfg(test)]
mod review_tests {
    use super::*;
    fn gz(bytes: &[u8]) -> Vec<u8> {
        let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
        encoder.write_all(bytes).unwrap();
        encoder.finish().unwrap()
    }
    #[test]
    fn gzip_budget_and_trailing_members() {
        assert!(decode_text(gz(&[1; 100]), 10).is_err());
        let mut data = gz(b"first");
        data.extend(gz(b"second"));
        assert_eq!(decode_text(data, 11).unwrap(), b"firstsecond");
        let mut invalid = gz(b"first");
        invalid.extend(b"junk");
        assert!(decode_text(invalid, 100).is_err());
    }
    fn audio(seconds: &str) -> Value {
        json!({"streams":[{"codec_type":"audio","channels":1,"sample_rate":"48000","duration":seconds}],"format":{"duration":seconds}})
    }
    #[test]
    fn validate_tracks_not_container_duration() {
        let video = json!({"streams":[{"codec_type":"video","width":101,"height":99,"duration":"2"}],"format":{"duration":"2"}});
        let mut result = json!({"streams":[{"codec_type":"video","width":102,"height":100,"duration":"2"},{"codec_type":"audio","channels":1,"sample_rate":"48000","duration":"3"}],"format":{"duration":"3"}});
        compare_tracks(&video, Some(&audio("3")), &result).unwrap();
        result["streams"][1]["duration"] = json!("1");
        assert!(compare_tracks(&video, Some(&audio("3")), &result).is_err());
        result["streams"][1]["duration"] = json!("3");
        result["streams"][1]["channels"] = json!(2);
        assert!(compare_tracks(&video, Some(&audio("3")), &result).is_err());
    }
    fn chunk(sig: &[u8; 4], header: u16, padding: u16) -> Vec<u8> {
        let mut data = vec![0; 32];
        data[..4].copy_from_slice(sig);
        data[4..8].copy_from_slice(&24u32.to_be_bytes());
        data[8..10].copy_from_slice(&header.to_be_bytes());
        data[10..12].copy_from_slice(&padding.to_be_bytes());
        data
    }
    #[test]
    fn usm_refuses_bad_padding_extra_streams_and_truncation() {
        let dir = tempfile::tempdir().unwrap();
        let p = dir.path().join("x.usm");
        let valid = chunk(b"@SFV", 24, 0);
        std::fs::write(&p, &valid).unwrap();
        assert!(!check_usm_channels(&p).unwrap());
        for data in [
            chunk(b"@SFV", 24, 1),
            chunk(b"@ALP", 24, 0),
            chunk(b"@SBT", 24, 0),
            valid[..31].to_vec(),
        ] {
            std::fs::write(&p, data).unwrap();
            assert!(check_usm_channels(&p).is_err());
        }
        let mut data = valid.clone();
        let mut other = valid;
        other[12] = 1;
        data.extend(other);
        std::fs::write(&p, data).unwrap();
        assert!(check_usm_channels(&p).is_err());
    }
    #[test]
    fn missing_wave_reference_must_not_fall_back_to_zero() {
        let mut bytes = b"AFS2".to_vec();
        bytes.extend([2, 4, 2, 0]);
        bytes.extend(1u32.to_le_bytes());
        bytes.extend(32u16.to_le_bytes());
        bytes.extend(0u16.to_le_bytes());
        bytes.extend(0u16.to_le_bytes());
        bytes.extend(32u32.to_le_bytes());
        bytes.extend(64u32.to_le_bytes());
        bytes.resize(64, 0);
        let mut track = cridecoder::acb::Track {
            cue_id: 0,
            name: "fixture".into(),
            wav_id: 0,
            enc_type: 2,
            is_stream: false,
            stream_awb_id: 0,
        };
        check_awb_references(&bytes, &[track.clone()]).unwrap();
        track.wav_id = 1;
        assert!(check_awb_references(&bytes, &[track]).is_err());
        bytes[12..14].copy_from_slice(&0u16.to_le_bytes());
        assert!(check_awb_references(&bytes, &[]).is_err());
    }
}
