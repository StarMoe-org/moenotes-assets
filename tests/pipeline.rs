mod support;
use moenotes_assets::{
    catalog::Catalog,
    config::Config,
    crypto,
    worker::{Input, Job},
};
use support::*;

#[test]
fn synthetic_worker_export_and_crc() {
    let dir = tempfile::tempdir().unwrap();
    let (cat, encrypted) = fixture();
    let c = Catalog::parse(&cat).unwrap();
    let target = c.target(KEY).unwrap().clone();
    let location = c.closure(KEY).unwrap().remove(0);
    let mut plain = encrypted;
    crypto::decrypt(&mut plain, "fixture.bundle", 0).unwrap();
    let path = dir.path().join("fixture.bundle");
    std::fs::write(&path, &plain).unwrap();
    assert_eq!(
        crypto::bundle_crc(&path, 1 << 20).unwrap(),
        location.options.as_ref().unwrap().crc
    );
    let job = Job {
        config: Config {
            cdn_root: "https://cdn.invalid".into(),
            ..Default::default()
        },
        target,
        inputs: vec![Input { location, path }],
        output: dir.path().join("out"),
    };
    let files = moenotes_assets::worker::run(&job).unwrap();
    assert_eq!(files.len(), 1);
    assert_eq!(
        std::fs::read(job.output.join(&files[0].name)).unwrap(),
        BODY
    );
}

#[test]
fn dependency_completion_order_does_not_change_export_ids() {
    let dir = tempfile::tempdir().unwrap();
    let (cat, _) = fixture();
    let c = Catalog::parse(&cat).unwrap();
    let target = c.target(KEY).unwrap().clone();
    let base = c.closure(KEY).unwrap().remove(0);
    let mut inputs = vec![];
    for (index, name, marker) in [(1, "CAB-first", b"alpha"), (2, "CAB-other", b"bravo")] {
        let mut payload = serialized();
        let mut at = 0;
        while let Some(pos) = payload[at..].windows(7).position(|v| v == b"fixture") {
            let start = at + pos;
            payload[start..start + 5].copy_from_slice(marker);
            at = start + 7;
        }
        // Keep the logical container path unchanged; only the object labels differ.
        for start in 0..payload.len() - INTERNAL.len() {
            if payload[start..start + INTERNAL.len()].starts_with(b"Assets/") {
                payload[start..start + INTERNAL.len()].copy_from_slice(INTERNAL.as_bytes());
                break;
            }
        }
        let (bytes, crc) = bundle_named(payload, name);
        let path = dir.path().join(format!("{index}.bundle"));
        std::fs::write(&path, &bytes).unwrap();
        let mut location = base.clone();
        location.id = index;
        let o = location.options.as_mut().unwrap();
        o.size = bytes.len() as u64;
        o.crc = crc;
        inputs.push(Input { location, path });
    }
    let mut job = Job {
        config: Config {
            cdn_root: "https://cdn.invalid".into(),
            ..Default::default()
        },
        target,
        inputs,
        output: dir.path().join("first"),
    };
    let first = moenotes_assets::worker::run(&job).unwrap();
    assert_eq!(first.len(), 2);
    job.inputs.reverse();
    job.output = dir.path().join("second");
    let second = moenotes_assets::worker::run(&job).unwrap();
    assert_eq!(
        serde_json::to_value(first).unwrap(),
        serde_json::to_value(second).unwrap()
    );
}

#[test]
fn catalog_and_container_corruption_rejected() {
    let (cat, _) = fixture();
    for n in [0, 4, 16, 31, cat.len() / 2] {
        assert!(Catalog::parse(&cat[..n]).is_err());
    }
    let dir = tempfile::tempdir().unwrap();
    let p = dir.path().join("bad.bundle");
    std::fs::write(&p, [0; 128]).unwrap();
    assert!(crypto::bundle_crc(&p, 1 << 20).is_err());
}

#[test]
fn texture_sprite_aliases_only_when_same_source() {
    let (cat, _) = fixture();
    let mut c = Catalog::parse(&cat).unwrap();
    let mut second = c.target(KEY).unwrap().clone();
    second.id += 1;
    second.resource_type = "UnityEngine.Sprite".into();
    c.locations.insert(second.id, second.clone());
    c.keys.get_mut(KEY).unwrap().push(second.id);
    assert!(c.target(KEY).is_ok());
    c.locations.get_mut(&second.id).unwrap().internal.push('x');
    assert!(c.target(KEY).is_err());
}

#[test]
fn worker_crc_and_configuration_limits_fail_closed() {
    let dir = tempfile::tempdir().unwrap();
    let (cat, mut raw) = fixture();
    let c = Catalog::parse(&cat).unwrap();
    let mut location = c.closure(KEY).unwrap().remove(0);
    location.options.as_mut().unwrap().crc ^= 1;
    crypto::decrypt(&mut raw, "fixture.bundle", 0).unwrap();
    let p = dir.path().join("a.bundle");
    std::fs::write(&p, raw).unwrap();
    let job = Job {
        config: Config {
            cdn_root: "https://cdn.invalid".into(),
            ..Default::default()
        },
        target: c.target(KEY).unwrap().clone(),
        inputs: vec![Input { location, path: p }],
        output: dir.path().join("out"),
    };
    assert!(
        moenotes_assets::worker::run(&job)
            .unwrap_err()
            .to_string()
            .contains("CRC")
    );
    let mut c = job.config;
    c.temp_bytes = 1 << 20;
    assert!(c.validate().is_err());
    c.temp_bytes = 20 << 30;
    c.workers = 0;
    assert!(c.validate().is_err());
}

#[test]
fn generated_encrypted_hca_to_aac_and_wrong_key() {
    use std::io::Cursor;
    let dir = tempfile::tempdir().unwrap();
    let samples: Vec<f32> = (0..4800).map(|i| ((i as f32) * 0.08).sin() * 0.2).collect();
    let mut hca = Cursor::new(Vec::new());
    cridecoder::HcaEncoder::new(
        cridecoder::HcaEncoderConfig::new(48000, 1).with_encryption(8594927479),
    )
    .unwrap()
    .encode(&samples, &mut hca)
    .unwrap();
    let mut builder = cridecoder::AcbBuilder::new();
    builder.add_track(cridecoder::TrackInput::new(
        "synthetic-tone",
        0,
        hca.into_inner(),
    ));
    let mut bank = Cursor::new(Vec::new());
    builder.build(&mut bank, None).unwrap();
    let path = dir.path().join("bank.acb");
    std::fs::write(&path, bank.into_inner()).unwrap();
    let location = moenotes_assets::catalog::Location {
        id: 1,
        key: "sound/test".into(),
        internal: "https://cdn.invalid/asset/Android/test".into(),
        provider: moenotes_assets::catalog::CRI.into(),
        resource_type: "CriWare.Assets.CriAtomAcbAsset".into(),
        dependencies: vec![],
        options: None,
    };
    let job = Job {
        config: Config {
            cdn_root: "https://cdn.invalid".into(),
            ..Default::default()
        },
        target: location.clone(),
        inputs: vec![Input { location, path }],
        output: dir.path().join("out"),
    };
    // Exercise the real executable: its worker creates media-exec child processes.
    let request = dir.path().join("job.json");
    std::fs::write(&request, serde_json::to_vec(&job).unwrap()).unwrap();
    assert!(
        std::process::Command::new(env!("CARGO_BIN_EXE_moenotes-assets"))
            .arg("worker")
            .arg(&request)
            .status()
            .unwrap()
            .success()
    );
    let result: moenotes_assets::worker::WorkerResult =
        serde_json::from_slice(&std::fs::read(request.with_extension("result.json")).unwrap())
            .unwrap();
    assert!(result.error.is_none(), "{:?}", result.error);
    assert_eq!(result.files[0].media_type, "audio/mp4");
    let mut wrong = job;
    wrong.config.cri_key += 1;
    wrong.output = dir.path().join("wrong");
    std::fs::write(&request, serde_json::to_vec(&wrong).unwrap()).unwrap();
    assert!(
        std::process::Command::new(env!("CARGO_BIN_EXE_moenotes-assets"))
            .arg("worker")
            .arg(&request)
            .status()
            .unwrap()
            .success()
    );
    let result: moenotes_assets::worker::WorkerResult =
        serde_json::from_slice(&std::fs::read(request.with_extension("result.json")).unwrap())
            .unwrap();
    assert!(result.error.is_some());
}

#[test]
fn mutated_catalog_never_panics() {
    let (c, _) = fixture();
    for pos in (0..c.len()).step_by(3) {
        let mut b = c.clone();
        b[pos] = 255;
        assert!(
            std::panic::catch_unwind(|| Catalog::parse(&b)).is_ok(),
            "{pos}"
        );
    }
}
