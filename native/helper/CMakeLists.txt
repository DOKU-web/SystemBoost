# sb_helper: the small elevated helper of the Windows app. See src/main.cpp for the security design.
cmake_minimum_required(VERSION 3.20)
project(sb_helper CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")   # no VC++ redistributable needed

# NVAPI (MIT) from NVIDIA's public repository, pinned by commit and by hash.
set(NVAPI_COMMIT 70d337db9186e968eab622f7e786de7e437faf3d)
set(NVAPI_DIR ${CMAKE_BINARY_DIR}/nvapi)
function(nvapi_file name hash)
  if(NOT EXISTS ${NVAPI_DIR}/${name})
    file(DOWNLOAD https://raw.githubusercontent.com/NVIDIA/nvapi/${NVAPI_COMMIT}/${name} ${NVAPI_DIR}/${name}
         EXPECTED_HASH SHA256=${hash} TLS_VERIFY ON)
  endif()
endfunction()
nvapi_file(nvapi.h 76bbb71107c1388134d41712bed56bb6d80ec426f9bb2c168e2d6941b90f16ca)
nvapi_file(nvapi_lite_salstart.h 9db770e52947b33d68b0c1f17bc21edd9d736b824ccecee2c5c527d48a89be93)
nvapi_file(nvapi_lite_common.h 3904baab8e8501a53746ddf4e5b13a2e1d5acd6ee50f8c3c2eadd8e4d62c55f2)
nvapi_file(nvapi_lite_sli.h 2a9c75faecb32bc7a5237a4b83cc28a6a488c75493e44bf28688abf3fec01a34)
nvapi_file(nvapi_lite_surround.h 197ad1e4762b2243904b0a13418747436ae70e4824099bf56be4804c4c29956f)
nvapi_file(nvapi_lite_stereo.h bd6fe69136cff36c73a9967a7cef37186791d9b7c3eb3ae7ac61b4efa628325b)
nvapi_file(nvapi_lite_d3dext.h 2322db28a2f7384ff6df6fd1110b48e99660fa29956a2cbcd28a9d7157017e1a)
nvapi_file(nvapi_lite_salend.h 92017f8e26f585601f4eaac475fa9e542eaa2f99a80e376dde52f2f73ab18e67)
nvapi_file(NvApiDriverSettings.h 95ffda903832840343a81189e052a0756774bd92e4b37f2dc49598d493f951d4)
nvapi_file(amd64/nvapi64.lib 901e479e548d41c30688c7481f0c6353c0be7fdee978796d27d87c32e19ce172)

add_executable(sb_helper src/main.cpp src/commands.cpp src/netpower.cpp src/nettop.cpp src/nvidia.cpp src/gpuoc.cpp src/driverlog.cpp src/fpsmeter.cpp src/fpslive.cpp)
target_include_directories(sb_helper PRIVATE ${NVAPI_DIR})
target_compile_definitions(sb_helper PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(sb_helper PRIVATE /W3 /utf-8)
target_link_libraries(sb_helper PRIVATE ${NVAPI_DIR}/amd64/nvapi64.lib advapi32 ole32 oleaut32 wbemuuid iphlpapi ws2_32 wevtapi)
# asInvoker: the app starts it elevated with the runas verb for the admin-only features, and plain for
# what the driver lets an ordinary user do, so a frame limit asks for nothing over a running game.
target_link_options(sb_helper PRIVATE "/MANIFESTUAC:level='asInvoker' uiAccess='false'" /SUBSYSTEM:WINDOWS /ENTRY:wmainCRTStartup)
