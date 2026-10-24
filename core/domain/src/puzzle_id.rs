//! Puzzle ids packed into a `u64`: up to eight ASCII characters, left-aligned and zero-padded,
//! so numeric order equals the byte order of the strings (the order of the Lichess file).

pub fn pack(id: &str) -> Option<u64> {
    let bytes = id.as_bytes();
    if bytes.is_empty() || bytes.len() > 8 || !bytes.iter().all(u8::is_ascii_alphanumeric) {
        return None;
    }
    let mut buf = [0u8; 8];
    buf[..bytes.len()].copy_from_slice(bytes);
    Some(u64::from_be_bytes(buf))
}

pub fn unpack(value: u64) -> String {
    value.to_be_bytes().iter().take_while(|&&b| b != 0).map(|&b| b as char).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_and_order() {
        for id in ["00008", "0000D", "zzzzz", "A", "12345678"] {
            assert_eq!(unpack(pack(id).unwrap()), id);
        }
        assert!(pack("00008").unwrap() < pack("0000D").unwrap());
        assert!(pack("0000D").unwrap() < pack("0000a").unwrap());
        assert!(pack("0000").unwrap() < pack("00000").unwrap());
        assert_eq!(pack(""), None);
        assert_eq!(pack("123456789"), None);
        assert_eq!(pack("ab-cd"), None);
    }
}
