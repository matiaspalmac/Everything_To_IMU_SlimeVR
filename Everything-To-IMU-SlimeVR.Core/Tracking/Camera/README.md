# Webcam Pose Source (Sprint H)

Camera-to-IMU tracker source. Converts BlazePose 3D world landmarks into 6 virtual SlimeVR
trackers: HIP, CHEST, LEFT_UPPER_LEG, RIGHT_UPPER_LEG, LEFT_FOOT, RIGHT_FOOT. Hands and head
remain on Quest controllers / HMD.

## Models

Place ONNX models under `Models/blazepose/` next to the executable:

- `pose_landmark_full.onnx` (256x256 NHWC input, 33-landmark output via `ld_3d:0` [1,195])
- `pose_detection.onnx` (128x128 input, SSD-style person bbox — used for ROI cropping in H1.5+)

Source: [PINTO_model_zoo / 053_BlazePose](https://github.com/PINTO0309/PINTO_model_zoo/tree/main/053_BlazePose), subfolder `30_Latest_after_consolidation/`. License: Apache-2.0 (`LICENSE.txt` in the model dir documents redistribution terms).

First-run download is not yet wired (see Sprint H6). For local dev, run `Models/blazepose/download.sh`-equivalent: pull `https://s3.ap-northeast-2.wasabisys.com/pinto-model-zoo/053_BlazePose/resources.tar.gz`, extract `30_Latest_after_consolidation/{01_pose_detection,03_pose_landmark_full_body}/saved_model_tflite_tfjs_coreml_onnx/model_float32.onnx` and rename.

The full variant lacks the metric-space `world_3d:0` tensor that the heavy variant exposes. Inference falls back to converting image-space landmarks to a normalised [-1..+1] cube; bone quaternions only need relative landmark vectors so units cancel. Heavy variant can be added later by running tf2onnx on `40_lite_full_heavy_version_May6_2021/saved_model_heavy.tar.gz`.

## Pipeline

```
WebcamCaptureLoop  (OpenCvSharp4, DSHOW backend, MJPG)
    -> Mat (BGR)
BlazePoseInference (ONNX RT DirectML)
    -> BlazePoseFrame (33 landmarks, world-space metres, hip-origin)
LandmarkProcessor   [H2]  -> SlimeVR coord space + One-Euro filter
BoneSolver          [H3]  -> 6 quaternions (Method A shortest-arc + Method B basis)
TPoseCalibration    [H4]  -> rest-pose delta
WebcamPoseTracker   [H5]  -> IBodyTracker x6 -> existing UDPHandler.SetSensorBundle
```

## Notes

- DirectShow backend is required on Windows 11 — Media Foundation has known enumeration bugs.
- MJPG fourcc is required to avoid USB bandwidth throttling at 720p+.
- Inference is one-in-flight: frames arriving during inference are dropped, not queued.
- Acceleration sent to SlimeVR is finite-differenced from position changes (optional; protocol
  tolerates rotation-only).
