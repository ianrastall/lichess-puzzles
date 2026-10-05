cmake_minimum_required(VERSION 3.24)
project(lpd_core VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
# Static CRT: the DLL then has no dependency on the VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)

add_library(lpd_core_objects OBJECT
    src/chess.cpp
    src/db.cpp
    src/engine.cpp
    src/history.cpp
    src/puzzle.cpp
    src/session.cpp)
target_compile_definitions(lpd_core_objects PRIVATE LPD_CORE_BUILD _CRT_SECURE_NO_WARNINGS)
if(MSVC)
    target_compile_options(lpd_core_objects PRIVATE /W4 /permissive- /utf-8)
endif()

add_library(lpd_core SHARED $<TARGET_OBJECTS:lpd_core_objects>)
target_include_directories(lpd_core PUBLIC include)

option(LPD_BUILD_TESTS "Build the core self-checks" ON)
if(LPD_BUILD_TESTS)
    enable_testing()
    add_executable(lpd_core_tests tests/core_tests.cpp $<TARGET_OBJECTS:lpd_core_objects>)
    target_compile_definitions(lpd_core_tests PRIVATE LPD_CORE_STATIC _CRT_SECURE_NO_WARNINGS)
    add_test(NAME core COMMAND lpd_core_tests ${CMAKE_CURRENT_SOURCE_DIR}/../samples/sample_puzzles.csv)
endif()
