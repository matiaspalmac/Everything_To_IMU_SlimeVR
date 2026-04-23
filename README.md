# Everything_To_IMU_SlimeVR

Bridge app that turns gamepads with IMU sensors (gyroscope + accelerometer) into SlimeVR body trackers. Connects a controller, runs sensor fusion locally, and streams the resulting rotation to a running SlimeVR Server over UDP.

Forked from [@Sebane1/Everything_To_IMU_SlimeVR](https://github.com/Sebane1/Everything_To_IMU_SlimeVR) (originally PS5_Dualsense_To_IMU_SlimeVR). This fork is a near-rewrite focused on tracking quality, Joy-Con 2 / Switch 2 support, and a modern WPF UI.

## What it does

- Reads IMU data from supported controllers via [JoyShockLibrary](https://github.com/JibbSmart/JoyShockLibrary), direct WinRT GATT for Switch 2 controllers, and parallel HID for high-rate Switch 1 streams.
- Applies factory calibration (Sony DualShock 4 / DualSense factory bias + per-axis sensitivity, plus JSL Stillness mode) before fusion.
- Fuses gyro + accel + magnetometer into an orientation quaternion using [VQF v2.1.1](https://github.com/dlaidig/vqf).
- Announces itself to a local SlimeVR Server (UDP 6969) as a virtual tracker per controller.
- Streams rotation + acceleration (m/s²) + battery so the tracker appears in the SlimeVR dashboard.
- Persists per-controller mounting rotation (90° increments) and gyro scale trim across sessions, keyed on the controller's stable MAC.
- Detects USB / Bluetooth controller plug events instantly via HidSharp and BLE adverts (no Rescan click).
- Optional haptic pipeline: routes OSC / bHaptics events back to controller rumble (PS-family + Joy-Con 2 simple presets).

Calibration, body-part assignment, reset / mounting, skeleton, and OSC forwarding are handled by SlimeVR Server itself — this app is just the bridge.

## Supported controllers

| Controller | Transport | IMU | Notes |
|---|---|---|---|
| PS5 DualSense (CFI-ZCT1) | USB / Bluetooth | BMI270-class | Primary target. Sony factory bias + per-axis cal applied; JSL Stillness drift estimator on. |
| PS5 DualSense Edge (CFI-ZEA1) | USB / Bluetooth | Same as DualSense | Edge buttons supported via JSL upstream. USB poll up to 1000 Hz. |
| PS4 DualShock 4 v1 (CUH-ZCT1) | USB / Bluetooth | Bosch BMI055 | Same factory cal pipeline. Reported as BMI160 in the SlimeVR dashboard (closest enum match). |
| PS4 DualShock 4 v2 (CUH-ZCT2) | USB / Bluetooth | Same as v1 | |
| Nintendo Switch Pro | USB / Bluetooth | ST LSM6DS3 | Uses parallel HID reader for 3-sample-per-packet decoding (≈200 Hz vs JSL's ~67 Hz). JSL applies Switch factory cal at SPI 0x6020 internally. |
| Joy-Con (L / R / Charging Grip) | Bluetooth | ST LSM6DS3 | Same multi-sample HID treatment. Right-side IMU axis flip. |
| **Joy-Con 2 (L / R)** | Bluetooth LE | TDK ICM-42670-P + AKM AK09919 mag | Native BLE GATT path (no Windows pairing dialog). 9DoF VQF fusion with magnetometer. Variant detection via flash 0x13012. Simple-preset rumble. |
| **Switch 2 Pro Controller** | Bluetooth LE | Same chips | Same BLE path. |
| **Switch 2 NSO GameCube** | Bluetooth LE | Same chips | Same BLE path. |
| Wii Remote + MotionPlus | Bluetooth | Discrete STM gyro + accel | Separate non-JSL pipeline via the WiiClient companion or local Bluetooth. |
| Nintendo 3DS | Network | LSM330DLC-equivalent | The 3DSClient companion homebrew pushes IMU over UDP. |

## Requirements

- **Windows 11** (build 22000 or later) — required for `BluetoothLEPreferredConnectionParameters.ThroughputOptimized`, which keeps the Joy-Con 2 BLE link at the 7.5 ms interval the controller expects. Without it the rate caps at ~16 Hz on Win 10 default.
- .NET 10 runtime (the published single-file .exe carries it self-contained).
- [SlimeVR Server](https://slimevr.dev/download) installed and running.
- A controller connected via USB or Bluetooth. **Do NOT** Windows-pair Joy-Con 2 — leave it unpaired and let the app discover it from the BLE advert.

## Build

```bash
dotnet build Everything-To-IMU-SlimeVR.sln -c Debug
```

Single-file release (what we ship):

```bash
dotnet publish Everything-To-IMU-SlimeVR.UI.WPF/Everything-To-IMU-SlimeVR.UI.WPF.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -o publish/
```

Native dependencies (`vqf.dll`, `JoyShockLibrary.dll`) live in `Everything-To-IMU-SlimeVR.Core/` and ship next to the .exe. Both can be rebuilt from upstream sources via the helper batch scripts in `/tmp/vqf-source/build-cmake.bat` and `/tmp/jsl-source/build-cmake.bat` (Visual Studio 2022 BuildTools + CMake required).

## Usage

1. Start SlimeVR Server.
2. Plug in or Bluetooth-pair your controller (or, for Joy-Con 2, just hold the sync button — the app discovers it via BLE advert).
3. Launch `Everything-To-IMU-SlimeVR.exe`. The Trackers page shows every connected controller with live status (Healthy / Laggy / No IMU / Disconnected), raw IMU sample rate, jitter, and battery.
4. The controller shows up as a tracker in the SlimeVR dashboard — assign a body part and calibrate from there.
5. Adjust **mount rotation** in 90° increments per controller from the Selected tracker panel; the offset persists across sessions on the controller's MAC.

## Project layout

- `Everything-To-IMU-SlimeVR.Core` — tracking pipeline, IMU fusion (VQF), Sony factory cal reader, parallel HID reader for Switch family, Joy-Con 2 BLE tracker + manager, OSC / haptics, configuration storage.
- `Everything-To-IMU-SlimeVR.UI.WPF` — WPF user interface (Fluent / Mica), tracker grid, settings, debug page, tray icon.
- `SlimeImuProtocol` — git submodule implementing the SlimeVR UDP protocol (handshake, rotation, acceleration, battery, sensor info).
- `Companions/3DSClient` — homebrew 3DS app that forwards its IMU over UDP.
- `Companions/WiiClient` — homebrew Wii app that forwards Wiimote IMU over UDP.

## Tracking quality features

- Sony factory calibration (DualShock 4 / DualSense): per-unit gyro bias + per-axis sensitivity + accel bias from HID feature reports 0x05 / 0x02. JSL doesn't apply these for Sony controllers, only Switch family.
- JSL Stillness mode (`JslSetAutomaticCalibration`): runtime gyro bias estimator that runs alongside our static factory cal.
- VQF v2.1.1 with rest-detection bug fix (Feb 2026 upstream).
- 50 ms warm-up gate (down from 200 ms) thanks to factory cal eliminating the ZRL drift window.
- Mount rotation persisted per MAC.
- Per-MAC gyro scale trim slider (Joy-Con 2 only — JSL devices already have factory cal).
- Magnetometer fusion (9DoF VQF) for Joy-Con 2 with Earth-field magnitude validation.
- Joy-Con 1 / Switch Pro multi-sample HID reader: recovers the 2 IMU samples per packet that JSL drops (≈3× effective sample rate).

## Diagnostics in the UI

- **IMU column**: raw sample rate from the controller (uncapped).
- **Rate column**: throttled send rate to SlimeVR (200 Hz cap).
- **Jitter column**: average angular delta between consecutive published quaternions. Healthy at rest is < 0.10°.
- **Mount column**: current 90° offset.
- **Health status** with explicit Laggy / No IMU readouts when sample rate falls below 60 Hz.
- **Color swatch** next to the controller name mirrors the controller's lightbar / LED state.
- **Tray notifications** on connect / disconnect (only when the main window is in the tray).

## License

Follows the upstream project's license. See `LICENSE.txt`.

## Video demos (upstream)

- <https://www.youtube.com/watch?v=AtjMtfv4T8c>
- <https://www.youtube.com/playlist?list=PLSUFJs_C2T4JIZElIYnImLD4RfzOyHjxV>
