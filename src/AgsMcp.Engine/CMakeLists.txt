# agsmcp.dll - AGS engine plugin. Build x86 (Win32) to match the 32-bit engine:
#   cmake -S . -B build -A Win32
#   cmake --build build --config Release
cmake_minimum_required(VERSION 3.15)
project(agsmcp CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(agsmcp SHARED agsmcp.cpp agsplugin.h json_min.h)
# The engine loads the plugin by the bare name "agsmcp" -> agsmcp.dll (no "lib" prefix).
set_target_properties(agsmcp PROPERTIES OUTPUT_NAME "agsmcp" PREFIX "")
target_link_libraries(agsmcp ws2_32)

if (MSVC)
    # _CRT_SECURE_NO_WARNINGS is defined in the source itself (before any include).
    target_compile_definitions(agsmcp PRIVATE WIN32_LEAN_AND_MEAN)
endif()
