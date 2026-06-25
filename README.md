# JustShowMe
<img width="400" height="400" alt="justshowme_icon" src="https://github.com/user-attachments/assets/804d259a-3104-45b6-a55d-92b0370686e6" />

[![License: MIT](https://img.shields.io/github/license/jameshansen/JustShowMe)](https://github.com/jameshansen/JustShowMe/blob/main/LICENSE)
[![Version](https://img.shields.io/github/v/release/jameshansen/JustShowMe?color=7a39fb)](https://github.com/jameshansen/JustShowMe/releases/latest)

## Webcam Privacy Filter Concept
With remote work becoming increasingly common, working and appearing on camera in video meetings in shared spaces, from a coffee shop to a living room is now normal.

From an ethics and technology policy standpoint, this creates a problem, as people may appear on video who have not consented as such, even more problematic if the meeting is being recorded.

While blurring the entire background is an option, this can sometimes create strange effects make your video feed less visually appealing.

"JustShowMe" provides the solution. Using OpenCV, other faces are identified when they appear and blurred selectively, allowing you to show everyone your beautiful home decor, or cool coffee shop you happen to be at, without impacting individuals privacy.

<img width="1000" height="634" alt="justshowme_demo" src="https://github.com/user-attachments/assets/9856bedf-e27e-44bc-ae72-dff84a3364f5" />

## Implementation

The project is a Visual Studio solution split into three components:

1. **`justshowme_cam`** (C++) - the **JustShowMe Virtual Webcam** DirectShow driver. Once registered it always appears in the camera list of any app (Zoom, Teams, browsers) and serves frames out of a shared-memory buffer. Forked from https://github.com/tshino/softcam (MIT Licensed).
2. **`justshowme_gui`** (C#/WPF) - the configuration GUI and the frame pump. While running it opens the configured real webcam, runs the filter on every frame, and pushes the result into the virtual camera. It shows a live **Before/After** preview inline and manages driver install/registration. The GUI must be running for filtering to happen.
3. **`justshowme_filter`** (C# DLL) - the swappable AI filter. The default implementation does **any-angle face detection (YuNet)**, **face recognition (SFace)** so an allowed person stays allowed across angles and after leaving frame, cross-frame tracking, and selective Gaussian blur. The GUI loads whichever DLL the user configures; the default is the `justshowme_filter.dll` beside the GUI exe.

Settings and the filter DLL path are stored in `%ProgramData%\JustShowMe\settings.ini` so every JustShowMe process reads the same config.

### Face detection (the filter)

Earlier versions used a single Haar cascade, which only detects forward-facing faces - turn your head and the blur fell off. The filter now detects faces at any angle (profiles, tilts, look-down) using **YuNet**, a small, fast DNN face detector, and is organised into focused classes for clarity:

| Class | Role |
|---|---|
| `IFaceDetector` | Common detector interface (`Detect(Mat) → boxes + landmarks`). |
| `YuNetFaceDetector` | **The YuNet wrapper/decoder.** Loads `face_detection_yunet_2023mar.onnx` via OpenCV's DNN module and decodes its raw outputs, including the 5 facial landmarks. |
| `SFaceRecognizer` | **The SFace wrapper.** Loads `face_recognition_sface_2021dec.onnx`, aligns each face from YuNet's landmarks, and turns it into a 128-D embedding so faces can be matched by identity. |
| `FaceTracker` | Lightweight IoU tracker - stable face ids + keeps a face blurred for ~3s after detection drops, so a turning head doesn't flash clear. Carries each face's last embedding through the dropout. |
| `BlurFaceFilter` | The `IFrameFilter`: detect → embed → track → pad boxes ~15% → Gaussian blur every face except those whose embedding matches an allowed one. |

**Why a hand-written YuNet decoder?** OpenCvSharp 4.11 doesn't ship the high-level `cv::FaceDetectorYN` wrapper, so `YuNetFaceDetector` reproduces its post-processing itself: per-stride priors (8/16/32), `score = sqrt(cls · obj)`, box decode, and non-max suppression. This keeps us on the OpenCvSharp DNN module we already have - no extra dependency. `SFaceRecognizer` does the same for the missing `cv::FaceRecognizerSF`: align-crop to the canonical 112×112 template, one forward pass, compare by cosine.

The model file `face_detection_yunet_2023mar.onnx` (~230 KB) is bundled next to the filter DLL (from the [OpenCV Zoo](https://github.com/opencv/opencv_zoo)) and is required - the filter reports a clear error if it's missing.

### Face recognition - why YuNet and SFace are complementary

Detection alone can't tell *who* a face is. The IoU tracker gives faces stable ids only while they stay roughly put frame-to-frame; the moment a person leaves and returns, the detector blinks for too long, or a test video loops, they jump position and get a **brand-new id**. If "allowed" were keyed on that id, the person would silently start getting blurred again - and a new "New Face" would keep reappearing in the list. That's a recognition problem, not a detection one.

**YuNet** (detect) and **SFace** (recognise) are a matched pair in the [OpenCV Zoo](https://github.com/opencv/opencv_zoo), designed to compose: YuNet emits 5 facial landmarks, and those landmarks are exactly what SFace needs to align a face before embedding it. So the same model that finds a face hands the recogniser everything it needs - no separate landmark step, no extra dependency (both run through the OpenCvSharp DNN module already in use).

With SFace, "allow this person" stores their **embedding**, not a frame id. Each face is matched against the allowed embeddings by cosine similarity (cutoff `≥ 0.40` - a touch stricter than SFace's tuned 0.363, because a wrong match here un-blurs the wrong person), so allowing survives angle changes, brief disappearances, and re-entry. The cutoff errs toward blurring: a missed match re-blurs a known face (harmless), it never reveals an unknown one. The same embedding match also de-duplicates the GUI's face list, so one person stays one row instead of a new entry each time their track id resets. To keep that reliable the face is aligned to SFace's template with a deterministic closed-form similarity fit over YuNet's 5 landmarks - so the same face yields a stable embedding frame to frame.

The model file `face_recognition_sface_2021dec.onnx` (~37 MB) is bundled next to the filter DLL the same way and is likewise required.

### Building

**Requirements:** Visual Studio 2019 or 2022 with the **Desktop development with C++** and **.NET desktop development** workloads.

You can build either way:

- **Visual Studio:** open `JustShowMe.sln` and build the **x64** configuration (the C++ driver is x64-only).
- **Command line:** run the build script from the repo root:

  ```powershell
  powershell -ExecutionPolicy Bypass -File .\build.ps1            # Debug (default)
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
  ```

  `build.ps1` finds MSBuild via `vswhere` (so it works on any machine with a suitable VS install) and builds the whole solution as x64.

All three projects output to a single `Debug\` (or `Release\`) folder in the repo root - a self-contained, distributable build with the GUI exe, both DLLs, the YuNet and SFace models, and the OpenCvSharp natives. Click **Install** in the GUI (elevates via `regsvr32`) to register the virtual camera, then **Start**.

> **Note:** the driver is an in-process COM DLL, so it can't be overwritten while any app has the virtual camera loaded. If a rebuild fails with `LNK1168: cannot open justshowme_cam.dll for writing`, close apps that use the camera (Zoom, Chrome, Teams, the Camera app) and build again.

### Releasing

To produce a clean, distributable build for a GitHub release:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

This script:

- **Builds Release|x64.** In Release, [Costura.Fody](https://github.com/Fody/Costura) embeds the managed dependencies (OpenCvSharp, WpfExtensions, System.*) directly into `justshowme_gui.exe`, so the release isn't littered with loose DLLs. Costura runs in **Release only** - Debug builds stay loose for fast iteration. `justshowme_filter.dll` is deliberately *excluded* from embedding so it remains a swappable file the GUI loads by path (it still resolves OpenCvSharp from the exe's embedded copy at runtime).
- **Stamps a build number.** A counter in `build.number` (starting at `0005`) is written into `BuildInfo.cs`, so the window title bar reads e.g. `JustShowMe - Privacy Webcam Filter - Build 0005`. The counter auto-increments after each release.
- **Stages only what's needed** (the exe, the driver and filter DLLs, the YuNet and SFace models, the native OpenCvSharp DLLs, and the LICENSE) and zips them to `justshowme_build<NNNN>.zip` in the repo root, ready to attach to a GitHub release.

Native DLLs (`OpenCvSharpExtern.dll`, the ffmpeg DLL) can't be embedded, so they ship as files directly beside the exe.

## AI Ethics & Technology Policy (from the creator, James Hansen)

As I am a Software Developer and hold an MA in Public Policy, this project was an experiment to create an example of how AI can be used to address ethical considerations in a positive way.

The focus of this project basically boils down to "consent."

While the app does store facial recognition data, it is retained and processed locally, and has the objective of a consent-positive effect, automatically excluding people from video who did not consent to being filmed.

As policymakers worldwide grapple with regulating AI systems I hope this project, in a very small way, demonstrates that privacy-preserving, user-controlled AI applications are not only possible but essential for maintaining public trust in emerging technologies.

## Special Thanks
TheDigitalArtist at Pixabay for the [User Icon Graphic](https://pixabay.com/vectors/icon-user-person-preference-choice-9798054/) that forms part of the application icon design.

The [YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) face-detection model (Shiqi Yu et al.), distributed via the OpenCV Zoo, used for any-angle face detection.

The [SFace](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface) face-recognition model (Yaoyao Zhong & Weihong Deng), distributed via the OpenCV Zoo, used to recognise allowed faces.

## License

This project is licensed under GPL v3
