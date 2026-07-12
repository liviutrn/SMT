# SnowRunner Unified

Unified Windows host for:

- SnowRunner chassis attitude and local acceleration at 60 Hz;
- configurable FlyPT UDP motion output (six floats: pitch, roll, yaw, surge, sway, heave);
- the validated physical-wheel reader;
- SMT v10 safe manual gearbox and gradual analog clutch;
- wheel-aware stall control through a versioned shared-memory bridge.

## Projects

- `SnowRunnerUnified.csproj` — the main Windows UI and runtime service.
- `Telemetry/` — latest validated SnowRunnerTelemetry60Hz core.
- `Wheelspin/` — latest validated GearAwdProbe4 wheel core.
- `SMT/` — latest SMT v10 safe source plus the Unified bridge.

## Configuration

The host saves settings atomically to:

`%LOCALAPPDATA%\SnowRunnerUnified\settings.json`

The UI exposes FlyPT endpoint and per-axis tuning, controller axes, pedal calibration,
keyboard/controller bindings, clutch curve, idle take-off, manual gearbox options and
all stall thresholds. Settings are applied live to the SMT plug-in through
`Local\SnowRunnerUnified.v1`.

## Safety rules

- Stall is never requested in neutral, without an active SMT vehicle, or while the chassis is moving.
- Movement of any proven driven wheel cancels the request.
- Until driven-wheel classification is available, movement of any physical wheel cancels the request (safe fallback).
- The removed Gear-4/PowerCoef experiment is not present.

## Build

The C# host builds with `.NET 8`:

`dotnet build SnowRunnerUnified.csproj -c Release`

The native plug-in is built from `SMT/SnowRunner Manual Transmission.sln`, Release x64,
with the Visual C++ v143 or newer build tools and Windows SDK.
