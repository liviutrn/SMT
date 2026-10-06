cmake_minimum_required(VERSION 3.20)
project(SteamVRMSQualityV2 LANGUAGES CXX)

add_subdirectory("${MINHOOK_ROOT}" minhook-build EXCLUDE_FROM_ALL)

add_library(SteamVRMSQualityV2Hook SHARED Hook.cpp)
target_compile_features(SteamVRMSQualityV2Hook PRIVATE cxx_std_17)
target_include_directories(SteamVRMSQualityV2Hook PRIVATE "${MINHOOK_ROOT}/include")
target_link_libraries(SteamVRMSQualityV2Hook PRIVATE minhook)
target_compile_definitions(SteamVRMSQualityV2Hook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(SteamVRMSQualityV2Hook PROPERTIES PREFIX "")

add_executable(SteamVRMSQualityV2 Injector.cpp)
target_compile_features(SteamVRMSQualityV2 PRIVATE cxx_std_17)
target_link_libraries(SteamVRMSQualityV2 PRIVATE bcrypt)
target_compile_definitions(SteamVRMSQualityV2 PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
