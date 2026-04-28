# Changelog

## v0.4.0 — 2026-04-28

Big stability + correctness pass driven by user feedback (4× JC1 + phone setup
crashing, immediate post-calibration drift, ankle yaw "ticks down" continuously).
Three sprints of audit-driven fixes: ~44 changes across the C# tracker pipeline,
the WPF UI, the SlimeImuProtocol submodule, and the 3DS / Wii companions. None
introduce new behaviour for users — every change either prevents a crash, fixes
a silent functional bug, or removes a memory leak.

### Joy-Con 1 / Switch Pro

- **Factory IMU calibration is now applied on the parallel HID path.** Previously
  the 3-sample-per-packet HID reader fed VQF raw scaled values without the
  per-unit gyro bias and sensitivity correction the Switch firmware applies.
  Each LSM6DS3-TR-C ships with a slightly different ZRL — visible to users as
  "ankle yaw ticks down constantly" on long sessions and post-calibration drift.
  `JoyCon1SpiCalibration` now reads SPI flash 0x6020 (factory) or 0x8026 (user
  override, magic 0xB2 0xA1) via subcommand 0x10 over the shared HID stream
  before the IMU loop starts. Falls back to nominal scaling when the read fails
  (clones, busy bus).
- Online gyro bias estimator (`GyroBiasCalibrator`) wired into both HID and JSL
  paths so residual / thermal drift is corrected on top of factory cal.
- `JslSetAutomaticCalibration(true)` is now enabled for ctype 1/2/3 (was Sony
  only). Layered with our online estimator they converge to zero where unneeded.
- Adaptive warm-up: 200 ms when JC1 HID path is active so VQF tilt + the first
  stillness window land before downstream consumes a quaternion. Sony pads
  and JC2 stay at 50 ms.
- Battery byte is now scraped from the IMU 0x30 packet stream — drops the
  third concurrent HID handle that `HidBatteryReader` opened every 30 s, which
  was contributing real BT stack pressure with 4× JC1 paired.
- Cal source surfaced in the debug page (factory-spi / user-spi /
  nominal-fallback) plus runtime gyro bias and JSL stillness offset.

### Joy-Con 2 / Switch 2 family

- Onboarding documentation. The Trackers page hint message now spells out the
  flow per controller family: PS5/DS4 = Windows pair, JC1/Pro = Windows pair,
  **JC2 = SYNC button only, do NOT use Windows Add Bluetooth**. A new
  "Connecting controllers" section in the README walks each transport plus
  JC2-specific trivia (advert timing, 5 GHz Wi-Fi crowding, reconnect loop).
- Fixed the cooldown race that could spawn duplicate `JoyCon2BleTracker`
  instances for one physical device when two BLE adverts landed in the same
  scheduling window. One lock now covers the cooldown + tracker-existence
  check + slot reservation.
- Reconnect timer waits up to 2 s on `JoyCon2Manager.Stop()` so an in-flight
  reconnect callback can't add a tracker into an already-cleared list.
- `_lastPacketHex` is only built when the debug page is open — was running
  unconditionally at ~120 Hz allocating a StringBuilder + String per frame
  for nobody.

### Crash fixes

- `VQFWrapper` owns its own internal lock + destroyed flag. The finalizer
  thread can race with a notify thread under abnormal teardown; previously
  the parent class's lock was the only guard, and finalization could call
  `VQF_Destroy` on a handle a notify thread was still using.
- `WiiTracker.NewPacketReceived` and `ThreeDsControllerTracker.NewPacketReceived`
  used to be `async void` event handlers. Any unhandled exception past the
  await tore down the entire process. Wrapped in try/catch routed through
  `OnTrackerError`. `WiiTracker._isAlreadyUpdating` is also cleared in `finally`
  so an exception between flag-set and flag-clear no longer freezes the tracker.
- `TrackersViewModel.RefreshList`, `DebugViewModel.RefreshAvailable`, and
  `AppServices.EnumerateAllTrackers` iterated the live `GenericTrackerManager`
  tracker lists from the UI thread while background discovery / cleanup
  threads mutated them. `InvalidOperationException` ("collection was
  modified") was a real race. All three now use the existing `Snapshot*`
  helpers under the registry lock.
- `Configuration.SaveDebounced` was an infinite-reschedule loop — the timer
  callback re-armed the timer instead of writing. Mount yaw, gyro trim, and
  `JoyCon2KnownAddresses` never persisted across sessions despite the README
  promising they did. Fixed; settings now actually save.
- `Configuration.SaveConfig` retries on `InvalidOperationException` when the
  serialiser hits a mid-mutation race with the UI thread. Previously the
  exception was swallowed silently and `config.json` stayed empty.
- `Configuration.LoadConfig` heals null collections post-deserialise so a
  hand-edited config file with one broken field doesn't NRE on the next
  `.Add` / `.TryGetValue`.

### Concurrency

- `OpenVRReader` serialises `OpenVR.Init` and the pose refresh window behind
  a single lock. With 8 tracker threads polling HMD / waist yaw per sample,
  the previous double-checked init could race-init OpenVR on overlapping
  ticks and double-write the matrix dictionary.
- `WiimoteStateTracker.ProcessPacket` now runs under a per-tracker lock so
  concurrent packets to the same tracker can't lose a `VQFWrapper` or trip
  "collection was modified" on `_calibrationSamples`.
- `SonyImuCalibration` and `HidBatteryReader` hold their global lock only
  around dictionary access. HID I/O (open + GetFeature, up to 500 ms timeout
  each) used to serialise every other tracker behind one mutex — about 2 s
  of head-of-line blocking on startup with 4 trackers paired.
- `JoyCon1HidImuReader.Stop()` joins the reader thread (200 ms timeout) so
  in-flight `SampleReady` callbacks can't fire stale samples post-Stop.

### Protocol

- **`UDPHandler._endpoint` is now per-instance.** Used to be a static string
  shared by every handler. The first handshake reply for any tracker
  clobbered the discovery target for every other handler — multi-tracker
  setups routinely lost packets after the second device connected.
- `UdpClient.Connect()` in-place rebind on discovery replaced with a full
  client swap via `ConfigureUdp`. The previous in-place rebind raced
  concurrent `SendAsync` calls mid-flight, producing intermittent
  `SocketException InvalidArgument`.
- `ConfigureUdp` re-entry guard so two concurrent calls (watchdog +
  receive-loop discovery) can't each create a new `UdpClient` and leak the
  loser.
- `_handshakeOngoingFlag` ownership tracked per-instance. Previously
  Dispose unconditionally cleared the flag, letting one handler's Dispose
  unlock discovery for everyone — two handlers could race into discovery
  immediately.
- `_cts` cancellation token wired through `ReceiveLoop`, `WatchdogLoop`,
  and `HeartbeatLoop`. In-flight `ReceiveAsync` / `SendAsync` are now
  cancelled deterministically on Dispose.
- `Dispose()` unsubscribes from the static `OnForceHandshake` /
  `OnForceDestroy` events (was leaking discovery-only handler delegates
  for the lifetime of the process).
- `BuildSensorInfoPacket` sends `sensorStatus = 1` (OK). Was hardcoded 0
  which the server mapped to DISCONNECTED on first registration.
- Handshake identifier string clipped at 255 bytes (was silently truncated
  by a `(byte)` cast and produced a length / payload mismatch).

### UI lifecycle

- `MainWindow` unsubscribes from `AppServices.{BatteryLowAlert,
  TrackerConnected,TrackerDisconnected}` on Closed.
- `App.OnStartup` no longer blocks the dispatcher up to 10 s on the
  update.xml checksum prefetch — moved to `Task.Run`.
- `MidiHapticsPlayer` is Stop+Disposed before being replaced; repeated
  Load MIDI clicks no longer accumulate parallel NAudio playback streams.
- `MainWindowViewModel` Tick has a reentry guard (`Interlocked.Exchange`)
  so a slow SlimeVR probe can't stack two `RefreshStatusAsync` chains.
  `StopBackgroundWork` shuts the timer on window Closed.
- `TrackersViewModel` and `DebugViewModel` implement `IDisposable`; the
  hosting Page calls Dispose on Unloaded so the 800 ms / 50 ms refresh
  timers actually stop on navigate-away. Each navigate-back used to leak
  one timer firing forever against an orphaned VM.
- `App.DispatcherUnhandledException` shows a one-shot MessageBox so users
  at least know their session hit something unexpected. Was silent.
- `LogsDialog` seeks to the tail of the file instead of loading the whole
  thing then trimming. A 2 GB log no longer freezes the dialog open.
- `HapticCalibratorDialog` skips the periodic pulse while intensity is 0.
  Was waking the rumble subsystem the moment it opened.
- `OscViewModel` and `HapticsViewModel` use `SaveDebounced` instead of
  synchronous `SaveConfig` (which now actually writes — see above).
- `UDPHapticDevice` replaced `Task.Run + Thread.Sleep` loops with
  `Task.Delay`. Threadpool no longer permanently parked under multiple
  haptic devices. Implements `IDisposable`.

### 3DS companion

- `getServerIp` always null-terminates the buffer and falls back to
  `DEFAULT_SERVER_IP` on any read failure. A missing or truncated
  config used to leave the buffer uninitialised and `inet_pton` silently
  produced 0.0.0.0 — the homebrew "ran" but no packets ever reached anyone.
- `inet_pton` return value is now checked. Bad config (BOM, garbage,
  `server=foo`) fails fast instead of streaming to nowhere forever.
- `ThreeDsControllerTracker.Recalibrate` looks up calibration by `_ip`
  instead of `DeviceMap.ElementAt(_index)` (where `_index` was never
  assigned). Every connected 3DS used to share the first device's
  calibration regardless of which physical handheld it was.

### Wii companion

- TCP `net_write` partial-write loop. `net_write` may return fewer
  bytes than requested; the previous single-shot send silently truncated
  17-byte controller frames and produced garbage IMU on the host.
- `setsockopt` called only after the socket return value is validated
  (was dereferencing `fd = -1` on `net_socket` failure).
- `usleep` divisor clamped to `[8, 100]` ms. The host could send 0
  (busy-loop, 100 % CPU) or 255 (4 fps).
- Makefile dropped the hardcoded `C:/devkitPro/libogc/include` so the
  build works on every devkitPro install (libogc fragment already
  injects `$(LIBOGC_INC)`).
- `config.txt` ships with a documented placeholder instead of `0.0.0.0`.

### Documentation

- README "Connecting controllers" section walks each transport.
- README tracking quality section updated to reflect SPI cal, online
  bias, adaptive warm-up.

---

## v0.3.0 — 2026-04-24

See git log between `60b3cb3..424ea23` and the v0.3.0 release notes.

## v0.2.x

See git history.
