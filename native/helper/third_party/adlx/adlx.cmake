# AMD ADLX SDK v2.0 interface headers, fetched at configure time from AMD's public repository and
# pinned by commit and by SHA-256 (like NVAPI in ../../CMakeLists.txt). Only included with
# -DSB_ADLX=ON. Read README.md in this folder first: ADLX is under AMD's own SDK licence, not MIT.
# Only these headers are compiled in: no ADLX source (ADLXHelper, Platform) is used, and
# amdadlx64.dll comes with the user's AMD driver (loaded from System32 only, src/amd_adlx.cpp).
set(ADLX_COMMIT 1ca2dd42f1df8cbd045489b7a20013a07692a446)   # tag v2.0
set(ADLX_DIR ${CMAKE_BINARY_DIR}/adlx)
function(adlx_header name hash)
  if(NOT EXISTS ${ADLX_DIR}/${name})
    file(DOWNLOAD https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/ADLX/${ADLX_COMMIT}/SDK/Include/${name}
         ${ADLX_DIR}/${name} EXPECTED_HASH SHA256=${hash} TLS_VERIFY ON)
  endif()
endfunction()
adlx_header(ADLX.h a7fd218b772f7955d5e88fbb14ceae5f3a281be5bdc7bdb6ae0372fc36dbbd36)
adlx_header(ADLXDefines.h ceddbbf3b1a2fe92c1614cc1fcef4d37a2d9d02668bfcef07f3596dde8e3b17f)
adlx_header(ADLXStructures.h e19a1384ed226b9da29848e625dd122fb3cf29bbc208df62dd244947087ef63e)
adlx_header(ADLXVersion.h 7532d1dc988602f6593ee632855d23e3057a25dce1da1a11a038b1265bd8bdd4)
adlx_header(I3DSettings.h d8e76c9f2bfd044baf3c9a9b0d9bf3772834717d823552ab5cb6bbaf20d45fd4)
adlx_header(I3DSettings1.h 96cafb020425c80359c510c9621d37f4e9ab7771abc38080d78a411be2129694)
adlx_header(IChangedEvent.h cca43de0a038a381cb937944441a7de7809d14fe5c935353b2623330a26527d2)
adlx_header(ICollections.h 2f38e038a12b000955d47345cbf33f5155eef874e7bdfe7431a1408080cde1f9)
adlx_header(IGPUAutoTuning.h a5cda3a440a7cf48e4aeb7fb1c21f468b26e9f10efeacf43a2f0418aabbd4f7d)
adlx_header(IGPUManualGFXTuning.h 67640c1d2c559e1444600bdeacd4cce9204675a84a129f16f4ee39469ba74cc1)
adlx_header(IGPUManualPowerTuning.h 081d81e629134d553f2c748152ee3f9ef107370d5eb615f4e5b006bd5d137597)
adlx_header(IGPUManualVRAMTuning.h 8661a47eb9f37610d0c69ef22d2291c86a31fc5f6420f7156b775fac2becc290)
adlx_header(IGPUTuning.h be772160c1d20586272a043eb7add050253c4e59c0ac96c0961fd74466d8270c)
adlx_header(IPerformanceMonitoring.h 250e86ab3bd655fee20c79166c160d59846e319f5a72b6c52bf012d26086053d)
adlx_header(ISystem.h 7fe4d9874b2f0a2c4852b4516d49ef1f2006c0b814b22ee6d2d986ae13c2fbd0)
adlx_header(ISystem1.h 1f8e73c2ec21dc9d66dbd0fcc5ad31ab2b6c5e4212e66e6a67b175c62ebae880)
adlx_header(ISystem2.h b9bbcf979e2588c06a7e7dac45a416e00ac190df145cb336a5c7f1430a281a82)
