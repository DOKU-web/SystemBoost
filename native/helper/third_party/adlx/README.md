# AMD ADLX SDK (used by sb_helper's AMD features)

No AMD file is kept in this folder or in this repository. `adlx.cmake` fetches 17 ADLX interface
headers at configure time, pinned by commit and by SHA-256, only when sb_helper is configured with
`-DSB_ADLX=ON`. The default is OFF: `amd.info` then answers status `off` and every other `amd.*`
command `err off` (`src/amd_none.cpp`), and the app shows "AMD 드라이버 기능은 아직 준비 중이에요"
instead of asking Radeon owners to update a driver that may be fine.

## Source

- Repository: https://github.com/GPUOpen-LibrariesAndSDKs/ADLX
- Release: tag `v2.0` (published 2026-09-20), the latest release on 2026-11-17
- Commit: `1ca2dd42f1df8cbd045489b7a20013a07692a446`
- Archive checked: https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/archive/refs/tags/v2.0.zip,
  5,950,681 bytes, SHA-256 `007460bf2c76f40ceca48c302a4512bc8961dccf61842caa2cf2b50298e4a676`
  (its zip comment is the commit above). The per-file hashes in `adlx.cmake` were taken from this
  archive and match the raw files at that commit.

## Licence

ADLX is distributed under AMD's own "ADLX SDK License Agreement" (`ADLX SDK License Agreement.pdf`
at the root of AMD's repository; every tag from v1.0 to v2.0 has it, none has an MIT licence).

## File hashes

SHA-256 of the 17 headers `adlx.cmake` fetches from `SDK/Include/` at commit `1ca2dd42f1df8cbd045489b7a20013a07692a446`
(the same values as in `adlx.cmake`, which checks them on download):

| File | SHA-256 |
|---|---|
| `ADLX.h` | `a7fd218b772f7955d5e88fbb14ceae5f3a281be5bdc7bdb6ae0372fc36dbbd36` |
| `ADLXDefines.h` | `ceddbbf3b1a2fe92c1614cc1fcef4d37a2d9d02668bfcef07f3596dde8e3b17f` |
| `ADLXStructures.h` | `e19a1384ed226b9da29848e625dd122fb3cf29bbc208df62dd244947087ef63e` |
| `ADLXVersion.h` | `7532d1dc988602f6593ee632855d23e3057a25dce1da1a11a038b1265bd8bdd4` |
| `I3DSettings.h` | `d8e76c9f2bfd044baf3c9a9b0d9bf3772834717d823552ab5cb6bbaf20d45fd4` |
| `I3DSettings1.h` | `96cafb020425c80359c510c9621d37f4e9ab7771abc38080d78a411be2129694` |
| `IChangedEvent.h` | `cca43de0a038a381cb937944441a7de7809d14fe5c935353b2623330a26527d2` |
| `ICollections.h` | `2f38e038a12b000955d47345cbf33f5155eef874e7bdfe7431a1408080cde1f9` |
| `IGPUAutoTuning.h` | `a5cda3a440a7cf48e4aeb7fb1c21f468b26e9f10efeacf43a2f0418aabbd4f7d` |
| `IGPUManualGFXTuning.h` | `67640c1d2c559e1444600bdeacd4cce9204675a84a129f16f4ee39469ba74cc1` |
| `IGPUManualPowerTuning.h` | `081d81e629134d553f2c748152ee3f9ef107370d5eb615f4e5b006bd5d137597` |
| `IGPUManualVRAMTuning.h` | `8661a47eb9f37610d0c69ef22d2291c86a31fc5f6420f7156b775fac2becc290` |
| `IGPUTuning.h` | `be772160c1d20586272a043eb7add050253c4e59c0ac96c0961fd74466d8270c` |
| `IPerformanceMonitoring.h` | `250e86ab3bd655fee20c79166c160d59846e319f5a72b6c52bf012d26086053d` |
| `ISystem.h` | `7fe4d9874b2f0a2c4852b4516d49ef1f2006c0b814b22ee6d2d986ae13c2fbd0` |
| `ISystem1.h` | `1f8e73c2ec21dc9d66dbd0fcc5ad31ab2b6c5e4212e66e6a67b175c62ebae880` |
| `ISystem2.h` | `b9bbcf979e2588c06a7e7dac45a416e00ac190df145cb336a5c7f1430a281a82` |

To update: change `ADLX_COMMIT` and every hash in `adlx.cmake` together.
