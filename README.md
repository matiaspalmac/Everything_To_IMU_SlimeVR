# Everything_To_IMU_SlimeVR

Bridge app that turns gamepads with IMU sensors (gyroscope + accelerometer) into SlimeVR body trackers. Connects a controller, runs sensor fusion locally, and streams the resulting rotation to a running SlimeVR Server over UDP.

Original project by [@Sebane1](https://github.com/Sebane1/PS5_Dualsense_To_IMU_SlimeVR).

## What it does

- Reads IMU data from supported controllers via [JoyShockLibrary](https://github.com/JibbSmart/JoyShockLibrary).
- Fuses gyro + accel into an orientation quaternion using the [VQF](https://github.com/dlaidig/vqf) filter.
- Announces itself to a local SlimeVR Server (UDP 6969) as a virtual tracker.
- Streams rotation + acceleration + battery so the tracker appears in the SlimeVR dashboard.
- Forwards controller button presses (triggers, grips, recenter) and thumbstick to SlimeVR.
- Optional haptic pipeline: routes OSC/bHaptics events back to the controller's rumble motors.

Calibration, body-part assignment, reset/mounting, skeleton, and OSC forwarding are handled by SlimeVR Server itself — this app is just the bridge.

## Supported controllers

| Controller | Status | Notes |
|---|---|---|
| PS5 DualSense | Good | Primary target. BMI270-class IMU. |
| PS4 DualShock 4 (v1/v2) | Good | Via JSL. |
| Nintendo Switch Pro | OK | LSM6DS3H IMU. |
| Joy-Con L | OK | |
| Joy-Con R | OK | Axis remap applied (Y+Z negated). |
| Wii Remote + MotionPlus | Basic | Separate non-JSL pipeline. |
| Nintendo 3DS (over network) | Basic | Companion 3DS app pushes IMU over UDP. |

## Requirements

- Windows 10/11 x64.
- .NET 10 SDK + runtime.
- [SlimeVR Server](https://slimevr.dev/download) installed and running.
- A controller connected (USB or Bluetooth).

## Build

```bash
dotnet build Everything-To-IMU-SlimeVR.sln -c Debug
```

Run `Everything-To-IMU-SlimeVR.exe` from `Everything-To-IMU-SlimeVR.UI.WPF/bin/Debug/net10.0-windows7.0/`.

## Usage

1. Start SlimeVR Server.
2. Plug in or pair your controller.
3. Launch Everything_To_IMU_SlimeVR.
4. The controller shows up as a tracker in the SlimeVR dashboard — assign a body part and calibrate from there.

## Project layout

- `Everything-To-IMU-SlimeVR.Core` — tracking pipeline, IMU fusion, HID battery reader, OSC/haptics.
- `Everything-To-IMU-SlimeVR.UI.WPF` — WPF user interface (Fluent/Mica).
- `SlimeImuProtocol` — git submodule implementing the SlimeVR UDP protocol.
- `Companions/3DSClient` — homebrew 3DS app that forwards its IMU over UDP.
- `Companions/WiiClient` — homebrew Wii app that forwards Wiimote IMU over UDP.

## License

Follows the upstream project's license. See `LICENSE.txt`.

## Video demos (upstream)

- <https://www.youtube.com/watch?v=AtjMtfv4T8c>
- <https://www.youtube.com/playlist?list=PLSUFJs_C2T4JIZElIYnImLD4RfzOyHjxV>
