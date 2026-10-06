cmake_minimum_required(VERSION 3.20)
project(SteamVRHalfRate LANGUAGES CXX)

add_executable(SteamVRHalfRate SteamVRHalfRate.cpp)
target_compile_features(SteamVRHalfRate PRIVATE cxx_std_17)
target_include_directories(SteamVRHalfRate PRIVATE "${OPENVR_ROOT}/headers")
target_link_directories(SteamVRHalfRate PRIVATE "${OPENVR_ROOT}/lib/win64")
target_link_libraries(SteamVRHalfRate PRIVATE openvr_api)
target_compile_definitions(SteamVRHalfRate PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
