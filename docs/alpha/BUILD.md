# Public build provenance

The experimental distribution is rebuilt from the exact frozen Rust production sources used for alpha 48. Public source hashes and executable checksums identify the released snapshot. No scanner-rule, threshold or source-geometry changes are made for publication.

The original local executable contains compiler/dependency source paths identifying its build environment. It remains local. The public executable uses Rust source-path remapping for both Windows path separator forms and a basename-only PDB reference. No PDB, compiler cache, private input, screenshot, scan bundle or original local runtime-freeze metadata is distributed.

Rust describes remapping as best effort: linker output and externally generated strings can require separate inspection. The release process also scans the final executable and every package member for private paths and project filenames. See the official [Rust compiler source-path remapping documentation](https://doc.rust-lang.org/nightly/rustc/remap-source-paths.html).

## Reproduce a Windows build

Use Rust 1.99.1 and MSVC build tools. Build from a clean checkout, retaining Cargo.lock. Apply source-prefix mappings to your own checkout, Cargo home and user-profile directories using `CARGO_ENCODED_RUSTFLAGS`; mappings are local environment values, not paths stored in this repository. Include both slash styles on Windows. A linker argument `/PDBALTPATH:openplantrace-rs.pdb` keeps the PE's PDB reference relative.

```powershell
$projectRoot = (Resolve-Path .).Path
$cargoDirectory = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
$pairs = @(
  @($env:USERPROFILE, 'build-home'),
  @($projectRoot, 'opt-source'),
  @($cargoDirectory, 'cargo-home')
)
$compilerFlags = @()
foreach ($pair in $pairs) {
  foreach ($prefix in @($pair[0], $pair[0].Replace('\', '/'))) {
    $compilerFlags += '--remap-path-prefix=' + $prefix + '=' + $pair[1]
  }
}
$compilerFlags += '-Clink-arg=/PDBALTPATH:openplantrace-rs.pdb'
$env:CARGO_ENCODED_RUSTFLAGS = $compilerFlags -join [char]31
cargo build --release --locked --jobs 2 --manifest-path rust/Cargo.toml --features onnx
```

Path remapping changes binary bytes, diagnostic paths and checksums. The public build receives its own checksum and distribution-parity checks; it is not advertised as byte-identical to the original executable. Private-corpus timing figures refer to the original sealed evaluation. The [QA report](PUBLIC-QA.md) records the final verification status.
