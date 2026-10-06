cmake_minimum_required(VERSION 3.20)
project(SteamVRMSMidpoint LANGUAGES CXX)

add_subdirectory("${MINHOOK_ROOT}" minhook-build EXCLUDE_FROM_ALL)

add_library(SteamVRMSMidpointHook SHARED Hook.cpp)
target_compile_features(SteamVRMSMidpointHook PRIVATE cxx_std_17)
target_include_directories(SteamVRMSMidpointHook PRIVATE "${MINHOOK_ROOT}/include")
target_link_libraries(SteamVRMSMidpointHook PRIVATE minhook)
target_compile_definitions(SteamVRMSMidpointHook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(SteamVRMSMidpointHook PROPERTIES PREFIX "")

add_executable(SteamVRMSMidpoint Injector.cpp)
target_compile_features(SteamVRMSMidpoint PRIVATE cxx_std_17)
target_link_libraries(SteamVRMSMidpoint PRIVATE bcrypt)
target_compile_definitions(SteamVRMSMidpoint PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
