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

## Licence: not MIT

ADLX is distributed under AMD's own "ADLX SDK License Agreement" (`ADLX SDK License Agreement.pdf`
at the root of AMD's repository; every tag from v1.0 to v2.0 has it, none has an MIT licence).
Downloading, copying or using the SDK counts as accepting it. Points that matter here:

- §2: a revocable licence to use the SDK internally to make software for use with AMD hardware, and
  to distribute it only in object code inside our software, under an end-user licence agreement
  that meets §3.
- §3 and Schedule B: that end-user agreement must at least prohibit transfer/duplication and reverse
  engineering of our software, carry AMD's warranty disclaimer and liability limit, require export
  compliance, tell the user the software is under a restricted licence for the licensed purpose
  only, make AMD a third-party beneficiary, and cover later updates.
- §4(b): the SDK files may not be distributed or published: hence nothing of AMD's in this public
  repository.
- §4(f): no use that would require the SDK to be licensed as open source.
- §12: the SDK and information related to it are to be kept confidential.
- §16: Texas law, courts of Travis County.

The owner accepted the agreement on 2026-11-17. `build.ps1 -Adlx` turns SB_ADLX on; the default stays OFF, and the
CI build does not pass it yet (0.8.17 ships without ADLX: Article 12-2 below binds earlier users only from
2027-01-01, and the owner decides when a release includes ADLX). SystemBoost's EULA carries the §3 terms in Article 12-2
("AMD 구성 요소"), in force 2027-01-01 for people who agreed to an earlier version (EULA Article 13: 30 days'
notice). Left out of Article 12-2 because they clash with a Korean consumer agreement: Schedule B's
confidentiality, the user's indemnity, Texas law and courts, feedback licence and US government clauses.
Whether AMD would see Article 12-2 as "substantially similar" to Schedule B has not been checked by a lawyer.
This is not legal advice.

## What is used, and how the DLL is loaded

- Only the 17 headers listed in `adlx.cmake`. ADLXHelper and SDK/Platform are not used: their
  loader (`SDK/Platform/Windows/WinAPIs.cpp`, `adlx_load_library`) calls `LoadLibraryEx` with
  `LOAD_LIBRARY_SEARCH_USER_DIRS | LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS
  | LOAD_LIBRARY_SEARCH_SYSTEM32`, which also searches the application folder.
- `src/amd_adlx.cpp` loads `LoadLibraryExW(L"amdadlx64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32)`
  and resolves ADLX's documented entry points itself (ADLXQueryVersion, ADLXInitialize2 or
  ADLXInitialize, the legacy-driver variants, ADLXTerminate). No DLL, missing entry points, or a
  driver ADLX starts only in legacy mode end as the status `no_dll` / `old_driver`.
- amdadlx64.dll comes with the user's AMD driver. It is not in this repository and not in the package.

To update: change `ADLX_COMMIT` and every hash in `adlx.cmake` together, and check the licence again.
