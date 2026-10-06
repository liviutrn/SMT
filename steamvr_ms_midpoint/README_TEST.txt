SteamVR Motion Smoothing Midpoint-Only Experiment
================================================

THIS BUILD IS VERSION LOCKED
----------------------------
It only injects into the exact vrcompositor.exe supplied by the user:

SteamVR: 2.18.2 (v1790893534)
vrcompositor.exe SHA-256:
5dabeae9a0ac12d7c47a3aed9576c0d5116039eafffb47756fd674605d1fe554

If SteamVR updates or the binary differs, the injector refuses to run.

What it changes
---------------
Reverse engineering of this exact binary found SteamVR's motion-vector frame
selection function at RVA 0x001217B0.

For successful Motion Smoothing selection it tracks:
  TargetVsyncId
  InputVsyncId
  ReferenceVsyncId

At 30 real FPS on a 120-Hz compositor:
  Input - Reference = 4 VSyncs
  synthetic target deltas are 1, 2, 3
  => 25%, 50%, 75% hallucinated frames

The hook preserves the native selector, then filters ONLY this case:
  delta 1 (25%) -> suppressed
  delta 2 (50%) -> allowed
  delta 3 (75%) -> suppressed

All other source/display ratios are returned unchanged.

Intended cadence
----------------
Physical PSVR2/compositor remains 120 Hz.
SteamVR per-app Frame Limit remains 30 FPS.

Desired presentation sequence:
  Real A
  pose-only reproject/repeat
  one SteamVR Motion Smoothing midpoint
  pose-only reproject/repeat
  Real B

This is the first direct scheduler-level experiment. It does not fake HMD
refresh, alter VSync reporting, or replace SteamVR's Motion Smoothing algorithm.

How to test
-----------
1. Restore NORMAL PSVR2Toolkit. Do not use earlier fake-60 Toolkit builds.
2. Start SteamVR.
3. Keep PSVR2 at 120 Hz.
4. For the game under test:
     Frame Limit = 30
     Motion Smoothing = Always On / Forced
     Additional prediction = 0 ms initially
5. Run SteamVRMSMidpoint.exe once.
6. Launch/test any SteamVR game.
7. Ctrl+Alt+I toggles the midpoint-only filtering live.

The hook is library-wide because it lives inside vrcompositor, not inside a game.

Verification
------------
SteamVRMSMidpoint.log appears beside the DLL.

Healthy 30->120 operation should show counters such as:
  gap4 > 0
  midpoint_pass > 0
  outer_suppressed > 0

If calls increase but gap4 stays 0, the current game/session is not presenting
the expected 30->120 Motion Smoothing relation.

If outer_suppressed increases but the visual cadence is wrong, send the log and
fresh vrcompositor.txt. That tells us the scheduler filter is definitely being
hit and the next issue is which image SteamVR uses on the suppressed 120-Hz slots.

Live A/B
--------
Ctrl+Alt+I:
  enabled=true  -> midpoint-only filtering
  enabled=false -> untouched native SteamVR behavior

Removal
-------
Restart SteamVR. Nothing on disk inside the SteamVR installation is modified.
Delete the helper folder when finished.
