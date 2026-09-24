use aes::cipher::{KeyIvInit, StreamCipher, StreamCipherSeek};
use anyhow::{Context, Result, ensure};
use sha2::{Digest, Sha256};
use std::io::{Read, Seek, SeekFrom};
use unity_rs_core::{
    bundle::{CompressionType, UnityFsBundle},
    source::Region,
};

pub fn digest(b: &[u8]) -> String {
    hex::encode(Sha256::digest(b))
}
pub fn builtin(name: &str) -> bool {
    let s = name.to_ascii_lowercase();
    s.contains("_monoscripts_")
        || s.contains("_unitybuiltinassets_")
        || s.ends_with("_monoscripts.bundle")
        || s.ends_with("_unitybuiltinassets.bundle")
}
pub fn decrypt(data: &mut [u8], name: &str, offset: u64) -> Result<()> {
    ensure!(
        !name.is_empty() && !name.contains(['/', '\\']),
        "invalid original basename"
    );
    let n = data.len().min(16384u64.saturating_sub(offset) as usize);
    if n == 0 {
        return Ok(());
    }
    let key = hex::decode("7372a4ee777db361ad896c99e408a182")?;
    let mut hash = Sha256::new();
    hash.update(hex::decode("ee24a70238e2a0e5")?);
    hash.update(name.as_bytes());
    let nonce = hash.finalize();
    let mut iv = [0; 16];
    iv[..8].copy_from_slice(&nonce[..8]);
    let mut cipher = ctr::Ctr64BE::<aes::Aes128>::new_from_slices(&key, &iv)
        .map_err(|_| anyhow::anyhow!("AES setup"))?;
    cipher.seek(offset);
    cipher.apply_keystream(&mut data[..n]);
    Ok(())
}
pub fn bundle_crc(path: &std::path::Path, max: u64) -> Result<u32> {
    let region = Region::from_file(path)?;
    let b = UnityFsBundle::open(&region)?;
    ensure!(
        b.header.size == region.len(),
        "UnityFS declared size mismatch"
    );
    let mut file = std::fs::File::open(path)?;
    let mut total = 0u64;
    let mut hash = crc32fast::Hasher::new();
    for block in &b.blocks {
        total = total
            .checked_add(block.uncompressed_size.into())
            .context("block size overflow")?;
        ensure!(total <= max, "bundle expanded budget");
        ensure!(
            u64::from(block.compressed_size) <= max && u64::from(block.uncompressed_size) <= max,
            "block budget"
        );
        file.seek(SeekFrom::Start(block.compressed_offset))?;
        let mut raw = vec![0; block.compressed_size as usize];
        file.read_exact(&mut raw)?;
        let plain = match block.compression {
            CompressionType::None => raw,
            CompressionType::Lz4 | CompressionType::Lz4Hc => {
                lz4_flex::block::decompress(&raw, block.uncompressed_size as usize)?
            }
            CompressionType::Lzma => {
                let mut out = BoundedVec {
                    data: Vec::new(),
                    max: block.uncompressed_size as usize,
                };
                lzma_rs::lzma_decompress(&mut std::io::Cursor::new(raw), &mut out)?;
                out.data
            }
            _ => anyhow::bail!("unsupported CRC compression"),
        };
        ensure!(
            plain.len() == block.uncompressed_size as usize,
            "block size mismatch"
        );
        hash.update(&plain);
    }
    Ok(hash.finalize())
}
struct BoundedVec {
    data: Vec<u8>,
    max: usize,
}
impl std::io::Write for BoundedVec {
    fn write(&mut self, b: &[u8]) -> std::io::Result<usize> {
        if b.len() > self.max.saturating_sub(self.data.len()) {
            return Err(std::io::Error::other("expanded size limit"));
        }
        self.data.extend_from_slice(b);
        Ok(b.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ctr_reads() {
        let raw = vec![7; 17000];
        let mut all = raw.clone();
        decrypt(&mut all, "a.bundle", 0).unwrap();
        for off in [0, 1, 15, 16, 16380, 16384] {
            let mut part = raw[off..off + 30].to_vec();
            decrypt(&mut part, "a.bundle", off as u64).unwrap();
            assert_eq!(part, all[off..off + 30]);
        }
        decrypt(&mut all, "a.bundle", 0).unwrap();
        assert_eq!(all, raw);
    }
    #[test]
    fn exceptions() {
        assert!(builtin("COMMON_MONOSCRIPTS_A.BUNDLE"));
        assert!(!builtin("a_monoscripts.bundle.tmp"));
    }
}
