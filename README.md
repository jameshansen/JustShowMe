# JustShowMe
<img width="400" height="400" alt="justshowme_icon" src="https://github.com/user-attachments/assets/804d259a-3104-45b6-a55d-92b0370686e6" />

**Download the latest version [here](https://github.com/jameshansen/JustShowMe/releases/latest).**

[![Version](https://img.shields.io/github/v/release/jameshansen/JustShowMe?color=7a39fb)](https://github.com/jameshansen/JustShowMe/releases/latest) [![License: MIT](https://img.shields.io/github/license/jameshansen/JustShowMe)](https://github.com/jameshansen/JustShowMe/blob/main/LICENSE)

On first run click the **Install** button to install the Virtual Webcam driver.

## Webcam Privacy Filter Concept
With remote work becoming increasingly common, working and appearing on camera in video meetings in shared spaces, from a coffee shop to a living room is now normal.

From an ethics and technology policy standpoint, this creates a problem, as people may appear on video who have not consented as such, even more problematic if the meeting is being recorded.

While blurring the entire background is an option, this can sometimes create strange effects make your video feed less visually appealing.

"JustShowMe" provides the solution. Using OpenCV, other faces are identified when they appear and blurred selectively, allowing you to show everyone your beautiful home decor, or cool coffee shop you happen to be at, without impacting individuals' privacy.

<img width="1000" height="643" alt="smartfill_demo" src="https://github.com/user-attachments/assets/32090fd0-1867-42f1-a10d-8e11542ff221" />

<img width="1000" height="643" alt="blur_demo_2" src="https://github.com/user-attachments/assets/339ea7d9-8a48-484d-965e-492b30dd18c8" />

## Implementation

The project is a Visual Studio solution split into three components:

1. **`justshowme_cam`** (C++) - the **JustShowMe Virtual Webcam** DirectShow driver. Once registered it always appears in the camera list of any app (Zoom, Teams, browsers) and serves frames out of a shared-memory buffer. Forked from https://github.com/tshino/softcam (MIT Licensed).
2. **`justshowme_gui`** (C#/WPF) - the configuration GUI and the frame pump. While running it opens the configured real webcam, runs the filter on every frame, and pushes the result into the virtual camera. It shows a live **Before/After** preview inline and manages driver install/registration. The GUI must be running for filtering to happen.
3. **`justshowme_filter`** (C# DLL) - the AI filter, `justshowme_filter.dll`, bundled beside the GUI exe. It does **any-angle face detection (YuNet)**, **face recognition (SFace)** so an allowed person stays allowed across angles and after leaving frame, cross-frame tracking, and per-person blur — or **smart-fill** erasure that replaces an unwanted person with recent background.

Settings and the filter DLL path are stored in `%ProgramData%\JustShowMe\settings.ini` so every JustShowMe process reads the same config.

### Face detection (the filter)

Earlier versions used a single Haar cascade, which only detects forward-facing faces - turn your head and the blur fell off. The filter now detects faces at any angle (profiles, tilts, look-down) using **YuNet**, a small, fast DNN face detector, and is organised into focused classes for clarity:

| Class | Role |
|---|---|
| `IFaceDetector` | Common detector interface (`Detect(Mat) → boxes + landmarks`). |
| `YuNetFaceDetector` | **The YuNet wrapper/decoder.** Loads `face_detection_yunet_2023mar.onnx` via OpenCV's DNN module and decodes its raw outputs, including the 5 facial landmarks. |
| `SFaceRecognizer` | **The SFace wrapper.** Loads `face_recognition_sface_2021dec.onnx`, aligns each face from YuNet's landmarks, and turns it into a 128-D embedding so faces can be matched by identity. |
| `FaceTracker` | Lightweight tracker giving stable face ids. Matches by IoU, then **falls back to SFace embedding** when boxes don't overlap (fast motion, edge jumps), so the same person keeps one track instead of leaving a trail of "ghost" regions. Keeps a face alive for a tunable window after detection drops (**Match loss sustain time**), so a turning head doesn't flash clear. |
| `BlurFaceFilter` | The `IFrameFilter`: detect → embed → track → obscure every person except those whose embedding matches an allowed one. **Mode** is selectable: blur the face, blur the whole person, or smart-fill (erase) the person with recent background. |

**Why a hand-written YuNet decoder?** OpenCvSharp 4.11 doesn't ship the high-level `cv::FaceDetectorYN` wrapper, so `YuNetFaceDetector` reproduces its post-processing itself: per-stride priors (8/16/32), `score = sqrt(cls · obj)`, box decode, and non-max suppression. This keeps us on the OpenCvSharp DNN module we already have - no extra dependency. `SFaceRecognizer` does the same for the missing `cv::FaceRecognizerSF`: align-crop to the canonical 112×112 template, one forward pass, compare by cosine.

The model file `face_detection_yunet_2023mar.onnx` (~230 KB) is bundled next to the filter DLL (from the [OpenCV Zoo](https://github.com/opencv/opencv_zoo)) and is required - the filter reports a clear error if it's missing.

### Face recognition - why YuNet and SFace are complementary

Detection alone can't tell *who* a face is. The IoU tracker gives faces stable ids only while they stay roughly put frame-to-frame; the moment a person leaves and returns, the detector blinks for too long, or a test video loops, they jump position and get a **brand-new id**. If "allowed" were keyed on that id, the person would silently start getting blurred again - and a new "New Face" would keep reappearing in the list.

**YuNet** (detect) and **SFace** (recognise) are a matched pair in the [OpenCV Zoo](https://github.com/opencv/opencv_zoo), designed to compose: YuNet emits 5 facial landmarks, and those landmarks are exactly what SFace needs to align a face before embedding it. So the same model that finds a face hands the recogniser everything it needs - no separate landmark step, no extra dependency (both run through the OpenCvSharp DNN module already in use).

With SFace, "allow this person" stores their **embedding**, not a frame id. Each face is matched against the allowed embeddings by cosine similarity (cutoff `≥ 0.40` - a touch stricter than SFace's tuned 0.363, because a wrong match here un-blurs the wrong person), so allowing survives angle changes, brief disappearances, and re-entry. The cutoff errs toward blurring: a missed match re-blurs a known face (harmless), it never reveals an unknown one. The same embedding match also de-duplicates the GUI's face list, so one person stays one row instead of a new entry each time their track id resets. To keep that reliable the face is aligned to SFace's template with a deterministic closed-form similarity fit over YuNet's 5 landmarks - so the same face yields a stable embedding frame to frame.

The model file `face_recognition_sface_2021dec.onnx` (~37 MB) is bundled next to the filter DLL.

### Mode: blur face, blur person, or smart-fill

The GUI's **Mode** (saved to the ini) chooses what happens to each acted-on person:

- **Blur Faces** — blur the padded face box.
- **Blur Person** — blur a whole-person region anchored on the face (~3 face-widths wide by default, from a face-height above the head down to the bottom of the frame). Since the face is the only part we can *identify*, the body is estimated from it rather than detected separately. It deliberately over-blurs a generous rectangle — for a privacy tool, covering too much is the safe error. A **Body zone size** slider scales it (up to 10 face-widths).
- **Smart Fill Person** — *erase* the person: replace their whole-person region with the background from a few seconds ago. A **Smart fill: go back** slider sets how far back (default 1 s). Designed for the "someone walks into shot" case — a second ago that space was empty, so they vanish into the real background.

Each allowed person's region is a **safe zone**: their original pixels are snapshotted before the others are obscured and painted back afterwards, so a neighbour's larger rectangle can't bleed over and obscure someone you chose to keep visible. (With rectangles this is imperfect — a hidden person directly behind an allowed one can show through the safe zone; per-pixel masks would resolve it.)

**How Smart Fill works:** the filter keeps a short rolling buffer of recent *clean* frames (full frames, up to ~6 s). To erase someone it copies their region from the buffered frame `go-back` seconds old. If the buffer isn't that deep yet (just switched on), it falls back to blurring so no one is left exposed.

### The allowed list — locked faces

When you **Add Face**, the chosen person is *locked*: the snapshots and embeddings captured at that moment are frozen and never overwritten by later frames or similar-looking people, and the same person can't be added twice. The Edit dialog shows the locked frames. The list is saved on exit to one file per face under `%ProgramData%\JustShowMe\facelist\` and restored on launch, so your allowed people persist across runs (removed faces are pruned). Blur/erase decisions are made against those locked embeddings, so "allowed" stays stable even as the live video changes.

### Tuning (GUI sliders, all saved to the ini)

| Control | What it does |
|---|---|
| **Match strictness** | Cosine cutoff for "same person". Raise if different people get merged / the wrong face is kept clear; lower if an allowed face keeps re-blurring. |
| **Match loss sustain time** | How long (seconds) a face stays tracked/blurred after detection drops. Lower to clear ghost regions faster; raise to hold through longer dropouts. |
| **Body zone size** | Width of the whole-person region (Blur Person / Smart Fill), in face-widths, up to 10. |
| **Smart fill: go back** | How many seconds of recent background to pull from when erasing a person. |
| **Snapshots per face** | How many recent frames to keep (and lock) per identity. |

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

- **Builds Release|x64.** In Release, [Costura.Fody](https://github.com/Fody/Costura) embeds the managed dependencies (OpenCvSharp, WpfExtensions, System.*) directly into `justshowme_gui.exe`, so the release isn't littered with loose DLLs. Costura runs in **Release only** - Debug builds stay loose for fast iteration. `justshowme_filter.dll` is deliberately *excluded* from embedding so it ships as a separate file the GUI loads at startup (it still resolves OpenCvSharp from the exe's embedded copy at runtime).
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

[Font Awesome](https://fontawesome.com/) for the button SVG icons.

The [stock](https://www.youtube.com/watch?v=WM9dkCgW3cM) [videos](https://www.youtube.com/watch?v=g0lMymp-FUc) used in the demo video above.

## License

This project is licensed under GPL v3
