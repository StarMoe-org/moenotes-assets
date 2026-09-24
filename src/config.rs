use anyhow::{Result, bail, ensure};
use serde::{Deserialize, Serialize};
use std::{net::SocketAddr, path::PathBuf};
use url::Url;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(default, deny_unknown_fields)]
pub struct Config {
    pub listen: SocketAddr,
    pub data_dir: PathBuf,
    pub region: String,
    pub cdn_root: String,
    pub locale: String,
    pub bili_version: String,
    pub downloads: usize,
    pub workers: usize,
    pub videos: usize,
    pub ffmpeg_threads: usize,
    pub queue_limit: usize,
    pub max_keys: usize,
    pub temp_bytes: u64,
    pub input_bytes: u64,
    pub output_bytes: u64,
    pub expanded_bytes: u64,
    pub memory_threshold: u64,
    pub worker_memory_bytes: u64,
    pub worker_timeout_secs: u64,
    pub download_timeout_secs: u64,
    pub ffmpeg: String,
    pub ffprobe: String,
    pub cri_key: u64,
    /// Local test servers only; never permits a non-loopback HTTP origin.
    pub allow_loopback_http: bool,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            listen: "127.0.0.1:8091".parse().unwrap(),
            data_dir: "data".into(),
            region: "hk".into(),
            cdn_root: String::new(),
            locale: "zh-Hant".into(),
            bili_version: "main".into(),
            downloads: 8,
            workers: 2,
            videos: 1,
            ffmpeg_threads: 4,
            queue_limit: 128,
            max_keys: 50_000,
            temp_bytes: 20 << 30,
            input_bytes: 1 << 30,
            output_bytes: 2 << 30,
            expanded_bytes: 2 << 30,
            memory_threshold: 32 << 20,
            worker_memory_bytes: 3 << 30,
            worker_timeout_secs: 900,
            download_timeout_secs: 120,
            ffmpeg: "ffmpeg".into(),
            ffprobe: "ffprobe".into(),
            cri_key: 8_594_927_479,
            allow_loopback_http: false,
        }
    }
}

impl Config {
    pub fn validate(&self) -> Result<()> {
        for (name, n, max) in [
            ("downloads", self.downloads, 64),
            ("workers", self.workers, 16),
            ("videos", self.videos, 16),
            ("ffmpeg_threads", self.ffmpeg_threads, 64),
            ("queue_limit", self.queue_limit, 4096),
            ("max_keys", self.max_keys, 50_000),
        ] {
            ensure!(n > 0 && n <= max, "invalid {name}");
        }
        for n in [
            self.temp_bytes,
            self.input_bytes,
            self.output_bytes,
            self.expanded_bytes,
            self.worker_memory_bytes,
        ] {
            ensure!((1 << 20..=1u64 << 40).contains(&n), "invalid byte budget");
        }
        ensure!(
            self.memory_threshold <= self.input_bytes,
            "memory threshold exceeds input limit"
        );
        ensure!(
            self.input_bytes + self.output_bytes + self.expanded_bytes * 2 <= self.temp_bytes,
            "temporary budget too small"
        );
        ensure!(
            (1..=3600).contains(&self.download_timeout_secs),
            "invalid download timeout"
        );
        ensure!(
            (1..=86400).contains(&self.worker_timeout_secs),
            "invalid worker timeout"
        );
        for (part, empty) in [
            (&self.region, false),
            (&self.locale, true),
            (&self.bili_version, false),
        ] {
            ensure!(
                (empty || !part.is_empty())
                    && part.len() <= 64
                    && part
                        .bytes()
                        .all(|c| c.is_ascii_alphanumeric() || b"_-".contains(&c)),
                "invalid configuration component"
            );
        }
        self.root()?;
        Ok(())
    }
    pub fn root(&self) -> Result<Url> {
        ensure!(
            !self.cdn_root.contains(['\\', '%', '?', '#']),
            "unsafe CDN root"
        );
        let u = Url::parse(&self.cdn_root)?;
        ensure!(
            u.username().is_empty() && u.password().is_none() && u.host_str().is_some(),
            "invalid CDN authority"
        );
        let loopback = u
            .host_str()
            .is_some_and(|h| h == "127.0.0.1" || h == "[::1]");
        ensure!(
            u.scheme() == "https" || (self.allow_loopback_http && loopback && u.scheme() == "http"),
            "HTTPS CDN required"
        );
        Ok(u)
    }
    pub fn catalog_url(&self, extension: &str) -> Result<Url> {
        ensure!(matches!(extension, "bin" | "hash"), "bad catalog extension");
        let root = self.root()?;
        let locale = if self.locale.is_empty() {
            String::new()
        } else {
            format!("_{}", self.locale)
        };
        Ok(Url::parse(&format!(
            "{}/asset/Android/catalog_{}{locale}.{extension}",
            root.as_str().trim_end_matches('/'),
            self.bili_version
        ))?)
    }
    pub fn asset_url(&self, internal: &str) -> Result<Url> {
        let original = Url::parse(internal)?;
        ensure!(
            matches!(original.scheme(), "http" | "https"),
            "unsupported local dependency"
        );
        ensure!(
            original.query().is_none()
                && original.fragment().is_none()
                && !internal.contains(['%', '\\'])
                && !internal.split('/').any(|p| p == ".." || p == "."),
            "unsafe asset URL"
        );
        let Some(at) = original.path().find("/asset/Android/") else {
            bail!("asset path missing")
        };
        let root = self.root()?;
        let out = Url::parse(&format!(
            "{}{}",
            root.as_str().trim_end_matches('/'),
            &original.path()[at..]
        ))?;
        ensure!(
            out.origin() == root.origin()
                && out.path().starts_with(&format!(
                    "{}/asset/Android/",
                    root.path().trim_end_matches('/')
                )),
            "asset outside CDN"
        );
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn url_boundary() {
        let c = Config {
            cdn_root: "https://cdn.invalid/prod".into(),
            ..Default::default()
        };
        c.validate().unwrap();
        assert_eq!(
            c.asset_url("https://dummy.net/asset/Android/a")
                .unwrap()
                .as_str(),
            "https://cdn.invalid/prod/asset/Android/a"
        );
        for u in [
            "file:///a",
            "https://dummy.net/asset/Android/../x",
            "https://dummy.net/asset/Android/%2e%2e/x",
            "https://dummy.net/asset/Android/a?x=1",
        ] {
            assert!(c.asset_url(u).is_err());
        }
    }
}
