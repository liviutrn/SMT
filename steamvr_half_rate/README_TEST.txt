SteamVRHalfRate - experimental Skyrim VR / SteamVR cadence test

Purpose
-------
Requests SteamVR's compositor-supported forced interleaved reprojection mode
FROM SkyrimVR.exe itself, while leaving the physical HMD refresh unchanged.

Test setup
----------
PSVR2 physical refresh: 120 Hz
SteamVR per-app Frame Limit: 30 FPS
Motion Smoothing: Always On / Forced
Additional prediction: leave at your normal value (0 ms is fine)

Install
-------
Copy SteamVRHalfRate.dll to:
  SkyrimVR\Data\SKSE\Plugins\

The plugin creates:
  SkyrimVR\Data\SKSE\Plugins\SteamVRHalfRate.log

It waits until SkyrimVR is the active scene-focus process, then calls:
  IVRCompositor::ForceInterleavedReprojectionOn(true)

Live A/B toggle:
  Ctrl + Alt + I

What to watch
-------------
We want to see whether the scene/motion-smoothing cadence becomes:
  30 real -> 60 scene output (one smoothed frame per real)
while the compositor / headset remains at 120 Hz.

If it still produces 30 -> 120, this API does not constrain the Motion
Smoothing hallucination cadence in SteamVR 2.18.2, and the next step is a
version-specific vrcompositor scheduler hook.

Removal
-------
Delete SteamVRHalfRate.dll. No SteamVR or Skyrim binary is modified.
