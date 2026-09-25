# SnowRunner Unified source baselines

Verified against the latest completed Codex task turns on 2026-07-12.

## Telemetry

- Task: `Build SnowRunner telemetry GUI`
- Final validated package: `SnowRunnerTelemetry60Hz`
- Source: `2026-07-09/goal-build-a-windows-desktop-gui/work/SnowRunnerTelemetry`
- Confirmed behavior: stable chassis orientation, local acceleration, 60 Hz acquisition and 60 Hz FlyPT UDP.
- `SnowRunnerTelemetryService.cs`: `110975` bytes, modified `2026-07-10 15:58:39`.

## Wheelspin

- Task: `Continue wheelspin finder`
- Latest source/package lineage: `SnowRunnerWheelspinFinder_GearAwdProbe4`
- Source: `2026-07-11/let/work/SnowRunnerWheelspinFinder`
- `WheelspinService.cs`: SHA-256 prefix `8BC5062A0BE039E1`, modified `2026-07-11 07:49:00`.
- `RootResolver.cs`: SHA-256 prefix `58DFFAC9A7C6F41C`, modified `2026-07-11 07:40:23`.
- `Trackers.cs`: SHA-256 prefix `53D9FB0AE95A9D60`, modified `2026-07-11 07:31:41`.

## SMT

- Task: `Continue wheelspin finder`
- Final validated release: `SMT-v10-safe-gradual-clutch`.
- Source: `2026-07-11/let/SMT-main/SMT-main`.
- Unsafe V9 Gear-4/PowerCoef experiment is not present.
- Defaults: bite start `0.10`, bite end `0.95`, curve `6.00`, pedal idle throttle `0.25`.
- `config.cpp`: SHA-256 prefix `0E2BBB7B164E3B61`.
- `input.cpp`: SHA-256 prefix `95E7039ADC91B215`.
- `game_data.cpp`: SHA-256 prefix `45377FBB472A2922`.
- `gui.cpp`: SHA-256 prefix `CBC329AF7D96B506`.
