SteamVR Prism Vector Fix v3
===========================

Target: exact user-supplied SteamVR 2.18.2 prism.dll.
Stock SHA-256:
2581419a64237e00c66881a9ff89a40b7ab6cfd3b7deebe738b68d66cb523f6f

This experiment NEVER suppresses generated frames. 30->120 remains:
REAL -> 25% generated -> 50% generated -> 75% generated -> REAL.

It does not modify the user's NVOFA helper or vrcompositor timing.

Why:
The embedded Prism "Motion Vector MeanMaxBlur" SPIR-V scans a 3x3 MV
neighborhood, computes the mean vector, separately chooses the neighboring
vector with the largest squared magnitude, then outputs:
    (mean + largest-magnitude-vector) / 2

That can spread foreground motion into nearby background at silhouettes even
with a stationary HMD. Prism's Frame Hallucination then forward-warps a mesh
using those vectors, so motion-boundary contamination becomes visible stretch.

MODE 1 - CENTER-PASS (test first)
The MeanMaxBlur stage outputs the center pixel's existing XY vector unchanged.
No vector attenuation, no frame rejection, no cadence change.

MODE 2 - PURE-MEAN
Outputs the 3x3 mean vector; removes only the max-motion bias. This is useful
to determine whether some local spatial smoothing is beneficial.

MODE 3 - RESTORE STOCK
Restores SHA-verified original prism.dll.

Usage:
1. EXIT SteamVR completely.
2. Run SteamVRPrismVectorFix.exe.
3. Choose 1.
4. Start SteamVR and test with the exact same NVOFA + 30->120 settings.
5. Compare against stock. To switch/restore: exit SteamVR and rerun tool.

The tool auto-finds:
C:\Games\Steam\steamapps\common\SteamVR\bin\win64\prism.dll
and also common default Steam locations.

It creates beside prism.dll:
prism.dll.prismfix-stock.bak

Expected hashes:
CENTER-PASS:
f7f3f3d864cf21d35c151daa8abeeef36beb2934bd8c8f537333e3f79d26672b
PURE-MEAN:
df4cd6c1442afaa2bf6a2559b2b18d6d0a3d2e88e8273877dbefd525422ef061

Unknown builds are refused.

Watch specifically:
- NPC/body silhouette stretch
- weapon/hand edge wobble
- foreground pulling background
- foliage/railings/thin geometry
- fast lateral object motion
- whether your NVOFA smoothness is preserved
