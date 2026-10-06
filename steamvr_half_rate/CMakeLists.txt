cmake_minimum_required(VERSION 3.20)
project(SteamVRHalfRate LANGUAGES CXX)

add_library(SteamVRHalfRate SHARED SteamVRHalfRate.cpp)
target_compile_features(SteamVRHalfRate PRIVATE cxx_std_17)
target_include_directories(SteamVRHalfRate PRIVATE "${OPENVR_HEADERS}")
target_compile_definitions(SteamVRHalfRate PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(SteamVRHalfRate PROPERTIES
    OUTPUT_NAME "SteamVRHalfRate"
    PREFIX ""
)
