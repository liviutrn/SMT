SteamVR Motion Smoothing Quality/Smoothness v2
================================================

Locked to the exact uploaded SteamVR 2.18.2 (v1790893534) vrcompositor.exe
SHA-256:
5dabeae9a0ac12d7c47a3aed9576c0d5116039eafffb47756fd674605d1fe554

v1 proved the exact 30->120 scheduler path:
  delta 1 = 25% generated frame
  delta 2 = 50% generated frame
  delta 3 = 75% generated frame

The midpoint-only v1 removed 25% and 75%. It reduced artifacts, but scene
motion became ~60 FPS. v2 tests better compromises.

HOTKEYS
-------
Ctrl+Alt+1 = NATIVE
  25%, 50%, 75% (stock SteamVR).

Ctrl+Alt+2 = SOFT FAR (DEFAULT, test this first)
  25%, 50%, 62.5%.
  The far 75% displacement is pulled halfway back toward midpoint.
  Keeps all three Motion Smoothing slots valid; avoids fallback.

Ctrl+Alt+3 = CLAMP FAR TO MIDPOINT
  25%, 50%, 50%.
  No prediction beyond midpoint, but the fourth 120-Hz slot still gets a
  valid MS result rather than selector failure/fallback.

Ctrl+Alt+4 = DROP 75 ONLY
  25%, 50%, fallback.
  Keeps two generated frames; likely ~90 unique scene states/s.

Ctrl+Alt+5 = MIDPOINT ONLY
  fallback, 50%, fallback.
  Exact proven v1 behavior.

Ctrl+Alt+I = A/B Native <-> last selected modified mode.

WHY 2/3 MAY IMPROVE V1
----------------------
The current v1 returns false on both outer slots. The caller then leaves the
Motion Smoothing vector path and takes its fallback path. Modes 2 and 3 instead
keep a valid MS result and shorten only the far prediction horizon. This may
retain more consistent 120-Hz pacing while reducing the most error-prone
extrapolation.

Exact selector disassembly also shows target-time displacement is linear in
output fields +0x004/+0x008/+0x00C and mirrors +0x2C4/+0x2C8/+0x2CC.
Reference/history-gap fields are separate and are NOT modified.

TEST
----
Restart SteamVR first if v1 is currently injected.
Normal PSVR2Toolkit.
PSVR2 120 Hz.
SteamVR per-app Frame Limit 30.
Motion Smoothing Always On / Forced.
Additional prediction 0 ms initially.

Start SteamVR, run SteamVRMSQualityV2.exe, launch any SteamVR game.

Suggested order:
  2 SoftFar -> 4 Drop75 -> 5 MidpointOnly -> 1 Native
Then 3 ClampFar if Drop75 has an obvious pacing hitch.

Log:
  SteamVRMSQualityV2.log
It records d1/d2/d3 plus soft/clamp/drop counters.

Removal: restart SteamVR. No SteamVR file is patched on disk.
