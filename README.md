# JustShowMe

> [!IMPORTANT]
> **JustShowMe is deprecated and no longer actively developed.** Its successor is
> **[VeBeGe](https://github.com/jameshansen/VeBeGe)** — the same background-removal
> idea, rebuilt to *just work*: no GUI and no "Add Face" button. VeBeGe runs as a
> quiet background service and registers a virtual twin of every webcam, so you
> just pick the **(VeBeGe)** camera in Zoom/Meet/Teams and anyone who isn't you is
> removed automatically. **👉 [Get VeBeGe here](https://github.com/jameshansen/VeBeGe).**
>
> This repository stays up for reference and history — it's where the real-time
> face detection, segmentation, and virtual-background pipeline behind VeBeGe was
> first proven out.

<img width="400" height="400" alt="justshowme_icon" src="https://github.com/user-attachments/assets/804d259a-3104-45b6-a55d-92b0370686e6" />

**Download the latest version [here](https://github.com/jameshansen/JustShowMe/releases/latest).**

[![Version](https://img.shields.io/github/v/release/jameshansen/JustShowMe?color=7a39fb)](https://github.com/jameshansen/JustShowMe/releases/latest) [![License: MIT](https://img.shields.io/github/license/jameshansen/JustShowMe)](https://github.com/jameshansen/JustShowMe/blob/main/LICENSE)

On first run click the **Install** button to install the Virtual Webcam driver.

## About and Concept
JustShowMe is a webcam video filter that lets you show your face, and your surroundings, but protects the privacy of the people around you.

With remote work becoming increasingly common, working and appearing on camera in video meetings in shared spaces, from a coffee shop to a living room is now normal.

From an ethics and technology policy standpoint, this creates a problem, as people may appear on video who have not consented as such, even more problematic if the meeting is being recorded.

While blurring the entire background is an option, this can sometimes create strange effects and makes your video feed less visually appealing.

"JustShowMe" provides the solution. Using face tracking, people are identified when they appear and either blurred or replaced with the background from previous video data, allowing you to show everyone your beautiful home decor, or cool coffee shop you happen to be at, without impacting individuals' privacy.

New in Build 0011, **Virtual Background**:

<img width="1014" height="753" alt="vb_demo" src="https://github.com/user-attachments/assets/ab6971ed-4636-4703-b18b-dce3526ea346" />


Smart Fill and Blur:

<img width="1000" height="643" alt="smartfill_demo" src="https://github.com/user-attachments/assets/32090fd0-1867-42f1-a10d-8e11542ff221" />

<img width="1000" height="643" alt="blur_demo" src="https://github.com/user-attachments/assets/c1e4e314-e755-4804-a965-476a2b07f06a" />

## Implementation

The project is a Visual Studio solution split into three components:

1. **`justshowme_cam`** (C++) - the **JustShowMe Virtual Webcam** DirectShow driver. Once registered it always appears in the camera list of any app (Zoom, Teams, browsers) and serves frames out of a shared-memory buffer. Forked from https://github.com/tshino/softcam (MIT Licensed).
2. **`justshowme_gui`** (C#/WPF) - the configuration GUI and the frame pump. While running it opens the configured real webcam, runs the filter on every frame, and pushes the result into the virtual camera. It shows a live **Before/After** preview inline and manages driver install/registration. The GUI must be running for filtering to happen.
3. **`justshowme_filter`** (C# DLL) - the AI filter, `justshowme_filter.dll`, bundled beside the GUI exe. It does **any-angle face detection (YuNet)**, **face recognition (SFace)** so an allowed person stays allowed across angles and after leaving frame, cross-frame tracking, and per-person blur - or **smart-fill** erasure that replaces an unwanted person with recent background.

When using **Add Face** the snapshots and embeddings are captured at that moment and used to exclude future appearances of the same face. The list is saved on exit to one file per face under `%ProgramData%\JustShowMe\facelist\` and restored on launch, so your allowed people persist across runs (removed faces are pruned).

Settings are stored in `%ProgramData%\JustShowMe\settings.ini`.

### Face detection (the filter)

Earlier versions used a single Haar cascade, which only detects forward-facing faces - turn your head and the blur fell off. The filter now detects faces at any angle (profiles, tilts, look-down) using **YuNet**, a small, fast DNN face detector, and is organised into focused classes for clarity:

| Class | Role |
|---|---|
| `IFaceDetector` | Common detector interface (`Detect(Mat) → boxes + landmarks`). |
| `YuNetFaceDetector` | **The YuNet wrapper/decoder.** Loads `face_detection_yunet_2023mar.onnx` via OpenCV's DNN module and decodes its raw outputs, including the 5 facial landmarks. |
| `SFaceRecognizer` | **The SFace wrapper.** Loads `face_recognition_sface_2021dec.onnx`, aligns each face from YuNet's landmarks, and turns it into a 128-D embedding so faces can be matched by identity. |
| `FaceTracker` | Lightweight tracker giving stable face ids. Matches by IoU, then **falls back to SFace embedding** when boxes don't overlap (fast motion, edge jumps), so the same person keeps one track instead of leaving a trail of "ghost" regions. Keeps a face alive for a tunable window after detection drops (**Match loss sustain time**), so a turning head doesn't flash clear. |
| `PersonSegmenter` | **PP-HumanSeg wrapper** (OpenCV Zoo). Loads `human_segmentation_pphumanseg_2023mar.onnx` through the same OpenCvSharp DNN module and returns a per-pixel **foreground mask** (white = person), the "Zoom virtual background" style isolation. Optional: if the model isn't present the mask features just stay off. |
| `VirtualBackgroundModel` | Owns the foreground/background split and the in-memory **virtual background**. Holds the `PersonSegmenter`, the current foreground mask, and the accumulated background plus a "known" mask recording which pixels have actually been revealed. Produces the background cutout used for detection, accumulates the people-free background, fills people regions from the known background, and composites the live foreground back on top with a feathered edge. |
| `BlurFaceFilter` | The `IFrameFilter`: detect, embed, track, then obscure every person except those whose embedding matches an allowed one. **Mode** is selectable: blur the face, blur the whole person, or smart-fill (erase) the person. It orchestrates the per-frame pipeline and delegates all segmentation and virtual-background work to `VirtualBackgroundModel`. |

**Why a hand-written YuNet decoder?** OpenCvSharp 4.11 doesn't ship the high-level `cv::FaceDetectorYN` wrapper, so `YuNetFaceDetector` reproduces its post-processing itself: per-stride priors (8/16/32), `score = sqrt(cls · obj)`, box decode, and non-max suppression. This keeps us on the OpenCvSharp DNN module we already have - no extra dependency. `SFaceRecognizer` does the same for the missing `cv::FaceRecognizerSF`: align-crop to the canonical 112×112 template, one forward pass, compare by cosine.

The model file `face_detection_yunet_2023mar.onnx` (~230 KB) is bundled next to the filter DLL (from the [OpenCV Zoo](https://github.com/opencv/opencv_zoo)) and is required - the filter reports a clear error if it's missing.

### Face recognition - why YuNet and SFace are complementary

Detection alone can't tell *who* a face is. The IoU tracker gives faces stable ids only while they stay roughly put frame-to-frame; the moment a person leaves and returns, the detector blinks for too long, or a test video loops, they jump position and get a **brand-new id**. If "allowed" were keyed on that id, the person would silently start getting blurred again - and a new "New Face" would keep reappearing in the list.

**YuNet** (detect) and **SFace** (recognise) are a matched pair in the [OpenCV Zoo](https://github.com/opencv/opencv_zoo), designed to compose: YuNet emits 5 facial landmarks, and those landmarks are exactly what SFace needs to align a face before embedding it. So the same model that finds a face hands the recogniser everything it needs - no separate landmark step, no extra dependency (both run through the OpenCvSharp DNN module already in use).

With SFace, "allow this person" stores their **embedding**, not a frame id. Each face is matched against the allowed embeddings by cosine similarity (cutoff `≥ 0.40` - a touch stricter than SFace's tuned 0.363, because a wrong match here un-blurs the wrong person), so allowing survives angle changes, brief disappearances, and re-entry. The cutoff errs toward blurring: a missed match re-blurs a known face (harmless), it never reveals an unknown one. The same embedding match also de-duplicates the GUI's face list, so one person stays one row instead of a new entry each time their track id resets. To keep that reliable the face is aligned to SFace's template with a deterministic closed-form similarity fit over YuNet's 5 landmarks - so the same face yields a stable embedding frame to frame.

The model file `face_recognition_sface_2021dec.onnx` (~37 MB) is bundled next to the filter DLL.

### Mode: virtual background, smart-fill, blur person, or blur faces

The GUI's **Mode** (saved to the ini) chooses what happens to the scene. The four options, in order:

- **Virtual Background** (default) - no per-person work. The whole background is replaced by the in-memory virtual background (people-free), and the live foreground is composited on top. Areas the virtual background has not learned yet keep the live frame, never black. This is the cleanest Zoom-style result and requires the Foreground / Background Mask, so selecting it auto-enables that.
- **Smart Fill People** - *erase* each background person: replace their whole-person region with recent background. A **Smart fill: go back** slider sets how far back (default 1 s) for the Rewind source. Designed for the "someone walks into shot" case.
- **Blur People** - blur a whole-person region anchored on the face (~3 face-widths wide by default, from a face-height above the head down to the bottom of the frame). Since the face is the only part we can *identify*, the body is estimated from it rather than detected separately. It deliberately over-blurs a generous rectangle - for a privacy tool, covering too much is the safe error. A **Body zone size** slider scales it (up to 10 face-widths).
- **Blur Faces** - blur the padded face box.

Each allowed person's region is a **safe zone**: their original pixels are snapshotted before the others are obscured and painted back afterwards, so a neighbour's larger rectangle can't bleed over and obscure someone you chose to keep visible. (With rectangles this is imperfect - a hidden person directly behind an allowed one can show through the safe zone; per-pixel masks would resolve it.)

**How Smart Fill works:** there are two modes (GUI toggle under **Settings** when Smart Fill is active):

- **Rewind Replace** (default) - the filter keeps a short rolling buffer of recent *clean* frames (full frames, up to ~6 s) and copies the erased region from the buffered frame `go-back` seconds old. Good for "someone walks into shot." If the buffer isn't deep enough yet it falls back to blurring so no one is left exposed.
- **Use Virtual Background** - replaces each background person with the in-memory virtual background (see **Smart Fill Virtual Background Pipeline** below). Where the real background behind them has already been seen it shows through cleanly; where it has not been seen yet it falls back to a blur, so no one is left exposed and no black is ever painted onto the output. Selecting this mode auto-enables the Virtual Background toggle (it depends on it).

### Foreground / Background Mask (person segmentation)

A **Foreground / Background Mask** toggle (under the Exclusion List) runs **PP-HumanSeg** to isolate the person sitting in front of the camera. It applies to every mode, not just Smart Fill: with the mask on, the filters (blur, smart fill) only ever touch the background, and the foreground (you) is never blurred or replaced. The same mask also builds the virtual background. While it is on, two previews appear: the **Foreground Mask** under the Before image (white = where it thinks the person is), and the **Virtual Background (in memory)** under the After image (the people-free background built up so far, transparent where nothing has been learned yet). The virtual background starts building as soon as the toggle is enabled, in any mode. A **Pad foreground** slider (default 0) dilates the mask by up to 40 px, growing the kept foreground a little so a tight cut-out includes a bit more around you.

### Smart Fill Virtual Background Pipeline

When the Virtual Background toggle is on, each frame is processed as two planes rather than one. This split is what lets Smart Fill replace background people without ever touching the webcam user.

1. **Segment.** PP-HumanSeg splits the input into a foreground mask (the webcam user) and the background.
2. **Detect on the background only.** Face detection runs on the *background cutout* (the frame with the foreground blacked out), so the webcam user is never detected and therefore can never be blurred or erased. Only background people are found. The tracker keeps them in memory across dropped detections, which gives the exclusion/replacement set.
3. **Build the virtual background.** The background is learned wherever there is no person, that is, neither the segmented foreground nor any detected background person, so nobody bakes in. Pixels seen for the first time are stored at full value; already-known pixels are smoothed with a running average. A "known" mask records which pixels have really been revealed, so unseen areas stay transparent and never paint black. A background-only scene-change check rebuilds the model if the camera moves.
4. **Compose the output.** Each background person is replaced by the known virtual background where it is available and blurred where it is not, then the live foreground is composited back on top with a feathered (soft) edge so the cut-out blends instead of showing a hard outline.

In short: input, split into foreground and background, detect and replace people on the background plane only, then lay the live foreground back over the result.


### Tuning (GUI sliders, all saved to the ini)

| Control | What it does |
|---|---|
| **Match strictness** | Cosine cutoff for "same person". Raise if different people get merged / the wrong face is kept clear; lower if an allowed face keeps re-blurring. |
| **Match loss sustain time** | How long (seconds) a face stays tracked/blurred after detection drops. Lower to clear ghost regions faster; raise to hold through longer dropouts. |
| **Pad foreground** | Dilates the foreground mask by up to 40 px so the kept foreground covers a little more around you. 0 = the raw segmentation mask. |
| **Body zone size** | Width of the whole-person region (Blur People / Smart Fill), in face-widths, up to 10. |
| **Smart fill: go back** | How many seconds of recent background to pull from when erasing a person. |
| **Snapshots per face** | How many recent frames to keep (and lock) per identity. |

A **16:9 / 4:3** button between the camera dropdown and Start toggles the capture aspect (default 16:9): the app requests a 16:9 capture mode from the webcam and falls back to 4:3 if none is available, and the virtual camera is sized to match so nothing is stretched.

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

## Future Improvement Plans
* Per-pixel masking everywhere: the allowed-person "safe zones" and the plain blur modes still use rectangles, where the Virtual Background pipeline already works per pixel. Bringing the mask to those paths would remove the show-through where a hidden person stands directly behind an allowed one.
* Multiple foreground people: PP-HumanSeg segments any prominent person as foreground, so a second person standing close is kept rather than replaced. Separating "the subject up front" from other close people would need depth or per-blob logic.
* Temporal smoothing of the mask: PP-HumanSeg runs per frame independently; a light frame-to-frame blend would steady the mask edge further.
* Improved body area detection: the current approach uses a rectangle extended from the face down. A better algorithm could isolate the actual shape of the body, but it would have to be optimised to run on everyday PCs.
* Improved temporal people tracking: track people's movement even when the face is lost, so coverage holds through turns and occlusions.

## AI Ethics & Technology Policy (from the creator, James Hansen)

As I am a Software Developer and hold an MA in Public Policy, this project was an experiment to create an example of how AI can be used to address ethical considerations in a positive way.

The focus of this project basically boils down to "consent."

While the app does store facial recognition data, it is retained and processed locally, and has the objective of a consent-positive effect, automatically excluding people from video who did not consent to being filmed.

As policymakers worldwide grapple with regulating AI systems I hope this project, in a very small way, demonstrates that privacy-preserving, user-controlled AI applications are not only possible but essential for maintaining public trust in emerging technologies.

## Special Thanks
TheDigitalArtist at Pixabay for the [User Icon Graphic](https://pixabay.com/vectors/icon-user-person-preference-choice-9798054/) that forms part of the application icon design.

The [YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) face-detection model (Shiqi Yu et al.), distributed via the OpenCV Zoo, used for any-angle face detection.

The [SFace](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface) face-recognition model (Yaoyao Zhong & Weihong Deng), distributed via the OpenCV Zoo, used to recognise allowed faces.

The [PP-HumanSeg](https://github.com/opencv/opencv_zoo/tree/main/models/human_segmentation_pphumanseg) human-segmentation model (PaddlePaddle / Baidu), distributed via the OpenCV Zoo, used for the Zoom-style foreground/background mask.

[Font Awesome](https://fontawesome.com/) for the button SVG icons.

The [stock](https://www.youtube.com/watch?v=WM9dkCgW3cM) [videos](https://www.youtube.com/watch?v=g0lMymp-FUc) used in the demo video above.

## License

This project is licensed under GPL v3
