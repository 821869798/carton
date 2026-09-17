# Vendored third-party source: Downloader

This directory contains a **vendored and trimmed copy** of the
[bezzad/Downloader](https://github.com/bezzad/Downloader) library.

| | |
|---|---|
| Upstream project | https://github.com/bezzad/Downloader |
| Upstream version | 5.9.5 (previously consumed as the `Downloader` NuGet package) |
| Licence | MIT — see [`LICENSE`](./LICENSE) in this directory |
| Copyright | Copyright (c) 2021 Behzad Khosravifar |
| Vendored on | 2026-09-14 |

`carton` itself is GPL-3.0-or-later (see the repository root `LICENSE`). The MIT licence is
GPL-compatible, but it requires that the copyright notice and permission notice be
retained in all copies or substantial portions of the software — which is why
`LICENSE` sits next to the sources here and must not be deleted.

## Why it is vendored instead of referenced

The NuGet package pulled in dependencies that carton does not use, and the library's
defaults are tuned for throughput on large files rather than for a low-memory desktop
tray app. Vendoring allows the unused surface to be removed and the memory-relevant
defaults to be corrected at the source.

## Local modifications

Changes relative to upstream 5.9.5 are intentional and must be preserved when
re-syncing:

- Unused features removed from the compiled surface, including download-request
  packages/serialization paths that carton never exercises.
- `#nullable disable` retained at the top of each file: the upstream code is not
  null-annotated, and enabling nullability here would produce hundreds of warnings
  in a dependency carton does not maintain.

## Re-syncing with upstream

1. Diff this directory against the target upstream tag before copying anything over.
2. Re-apply the local modifications listed above.
3. Keep `LICENSE` in place.
4. Update the "Upstream version" row in this file.

## Memory-relevant configuration

Two settings dominate the peak memory of a download and are configured by
`carton.Core/Services/AcceleratedFileDownloader.cs`, not here:

- **`MaximumMemoryBufferBytes`** — `ConcurrentPacketBuffer` treats `0` as
  `long.MaxValue`, i.e. *unbounded*. With parallel chunks on a fast link and a slower
  disk, producer threads outrun the single writer and the in-flight queue grows without
  limit, so a large archive can transiently pin its own size in pooled byte arrays.
  carton caps this so the queue applies backpressure instead.
- **`BufferBlockSize`** — multiplied by the active chunk count for the read buffers.
