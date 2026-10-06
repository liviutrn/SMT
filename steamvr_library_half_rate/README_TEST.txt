SteamVRHalfRate - library-wide experiment
========================================

Purpose
-------
A standalone OpenVR background client that asks SteamVR to force its
interleaved-reprojection mode globally while this helper remains running.

It is NOT Skyrim-specific and does not install into any game.

Recommended first test
----------------------
PSVR2 physical refresh: 120 Hz
SteamVR per-app Frame Limit: 30 FPS
Motion Smoothing: Always On / Forced
Additional prediction: 0 ms initially

Run order
---------
1. Start SteamVR normally.
2. Run SteamVRHalfRate.exe.
3. Start any SteamVR/OpenVR game.
4. Test.
5. Press Ctrl+Alt+I to toggle the override live for A/B comparison.
6. Ctrl+C or close the helper to exit; it explicitly turns the override off.

Files
-----
SteamVRHalfRate.exe
openvr_api.dll
SteamVRHalfRate.log is created beside the EXE.

Expected log
------------
OpenVR initialized as background client...
ForceInterleavedReprojectionOn(true) [startup]
Scene focus PID changed...
ForceInterleavedReprojectionOn(true) [scene-focus-change]

Goal
----
Test whether SteamVR 2.18.2 can be made to use a 60-Hz interleaved scene
cadence under the unchanged 120-Hz compositor, so a separately throttled
30-FPS application gets only one Motion Smoothing-generated intermediate
frame rather than three.

Important
---------
This is an experiment. ForceInterleavedReprojectionOn is a legacy SteamVR
compositor control. It may simply invoke legacy interleaved reprojection,
or it may interact with Motion Smoothing differently than desired.

If the visible 30->120 Motion Smoothing cadence does not change, the next
step is a version-specific vrcompositor 2.18.2 scheduler hook using the
exact binaries supplied by the user.

Removal
-------
Close/delete the helper. It does not patch SteamVR or any game files.
