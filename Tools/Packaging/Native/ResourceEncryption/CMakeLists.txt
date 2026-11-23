cmake_minimum_required(VERSION 3.22)
project(YLResourceEncryption LANGUAGES C CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "The bundled Unity backend is Windows x64 MSVC only")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(TINK_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(OPENSSL_NO_ASM ON CACHE BOOL "" FORCE)

include(FetchContent)
FetchContent_Declare(tink_pin
  URL https://codeload.github.com/tink-crypto/tink-cc/tar.gz/6e9682859227bbe3cb93db4a36ff929263dc1517
  URL_HASH SHA256=3e4587e072c0f944ba3a8d6cdb3372e5a7c94aa44bf69b9b2c20bb756da07f42)
FetchContent_GetProperties(tink_pin)
if(NOT tink_pin_POPULATED)
  if(POLICY CMP0169)
    cmake_policy(SET CMP0169 OLD)
  endif()
  FetchContent_Populate(tink_pin)
endif()
add_subdirectory("${tink_pin_SOURCE_DIR}" "${tink_pin_BINARY_DIR}" EXCLUDE_FROM_ALL)

add_library(ylresource_tink SHARED StreamingBridge.cc)
target_include_directories(ylresource_tink PRIVATE "${tink_pin_SOURCE_DIR}")
target_link_libraries(ylresource_tink PRIVATE tink::subtle::aes_gcm_hkdf_streaming tink::util::buffer absl::statusor absl::strings)
