cmake_minimum_required(VERSION 3.20)
project(sb_framegen LANGUAGES CXX)

# SystemBoost FrameGen v0.6
#   Windows 10 2004+ / Windows 11, MSVC 2022 + Windows SDK 10.0.19041 or newer.
#   cmake -B build -A x64 && cmake --build build --config Release
#   Output: build/Release/sb_framegen.exe + build/Release/shaders/*.cso

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(sb_framegen
    src/main.cpp src/Gpu.cpp src/Capture.cpp src/CaptureWgc.cpp
    src/FrameGenGpu.cpp src/Overlay.cpp src/Cursor.cpp)
target_compile_definitions(sb_framegen PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
    WINVER=0x0A00 _WIN32_WINNT=0x0A00)
target_link_libraries(sb_framegen PRIVATE d3d11 dxgi d3dcompiler dcomp dwmapi user32 gdi32 shell32 ole32 advapi32)

if(MSVC)
    # Windows.Graphics.Capture through C++/WinRT (ships with the Windows SDK)
    target_compile_definitions(sb_framegen PRIVATE SBFG_WITH_WGC)
    target_link_libraries(sb_framegen PRIVATE windowsapp)
    target_compile_options(sb_framegen PRIVATE /W4 /permissive- /EHsc /utf-8)
    set_property(TARGET sb_framegen PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_link_options(sb_framegen PRIVATE /SUBSYSTEM:CONSOLE)
    target_sources(sb_framegen PRIVATE sb_framegen.rc)
endif()

# Precompile shaders (cs_5_0) with fxc from the Windows SDK. At runtime the exe
# loads shaders/*.cso; if missing it compiles shaders/*.hlsl (dev fallback).
file(GLOB FXC_CANDIDATES "C:/Program Files (x86)/Windows Kits/10/bin/10.*/x64/fxc.exe")
list(SORT FXC_CANDIDATES ORDER DESCENDING)  # newest SDK first
find_program(FXC fxc PATHS "$ENV{WindowsSdkVerBinPath}/x64" ${FXC_CANDIDATES})
if(NOT FXC AND FXC_CANDIDATES)
    list(GET FXC_CANDIDATES 0 FXC)
endif()
set(SHADERS luma_down pyr_down motion_search motion_median static_map interpolate upscale sharpen)
set(CSO_FILES)
foreach(S ${SHADERS})
    set(OUT ${CMAKE_BINARY_DIR}/$<CONFIG>/shaders/${S}.cso)
    if(FXC)
        add_custom_command(OUTPUT ${OUT}
            COMMAND ${CMAKE_COMMAND} -E make_directory ${CMAKE_BINARY_DIR}/$<CONFIG>/shaders
            COMMAND ${FXC} /nologo /T cs_5_0 /E main /O3 /WX /I ${CMAKE_SOURCE_DIR}/shaders
                    /Fo ${OUT} ${CMAKE_SOURCE_DIR}/shaders/${S}.hlsl
            DEPENDS ${CMAKE_SOURCE_DIR}/shaders/${S}.hlsl ${CMAKE_SOURCE_DIR}/shaders/common.hlsli
            COMMENT "fxc ${S}.hlsl")
        list(APPEND CSO_FILES ${OUT})
    endif()
endforeach()
if(CSO_FILES)
    add_custom_target(sb_framegen_shaders ALL DEPENDS ${CSO_FILES})
    add_dependencies(sb_framegen sb_framegen_shaders)
else()
    message(WARNING "fxc not found: shipping .hlsl, compiled at startup")
    add_custom_command(TARGET sb_framegen POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_SOURCE_DIR}/shaders $<TARGET_FILE_DIR:sb_framegen>/shaders)
endif()
