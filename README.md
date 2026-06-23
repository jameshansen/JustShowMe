# JustShowMe
<img width="400" height="400" alt="justshowme_icon" src="https://github.com/user-attachments/assets/804d259a-3104-45b6-a55d-92b0370686e6" />

Webcam Privacy Filter

## Concept
With remote work becoming increasingly common, working and appearing on camera in video meetings in shared spaces, from a coffee shop to a living room is now normal.

From an ethics and technology policy standpoint, this creates a problem, as people may appear on video who have not consented as such, even more problematic if the meeting is being recorded.

While blurring the entire background is an option, this can sometimes create strange effects make your video feed less visually appealing.

"JustShowMe" provides the solution. Using OpenCV, other faces are identified when they appear and blurred selectively, allowing you to show everyone your beautiful home decor, or cool coffee shop you happen to be at, without impacting individuals privacy.

## Implementation

The project is a Visual Studio solution split into three components:

1. **`justshowme_cam`** (C++) — the **JustShowMe Virtual Webcam** DirectShow driver. Once registered it always appears in the camera list of any app (Zoom, Teams, browsers) and serves frames out of a shared-memory buffer. Forked from https://github.com/tshino/softcam (MIT Licensed).
2. **`justshowme_gui`** (C#/WPF) — the configuration GUI and the frame pump. While running it opens the configured real webcam, runs the filter on every frame, and pushes the result into the virtual camera. It shows a live **Before/After** preview inline and manages driver install/registration. The GUI must be running for filtering to happen.
3. **`justshowme_filter`** (C# DLL) — the swappable AI filter. The default implementation does Haar-cascade face detection (OpenCV) + selective Gaussian blur. The GUI loads whichever DLL the user configures; the default is the `justshowme_filter.dll` beside the GUI exe.

Settings and the filter DLL path are stored in `%ProgramData%\JustShowMe\settings.ini` so every JustShowMe process reads the same config.

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

All three projects output to a single `Debug\` (or `Release\`) folder in the repo root — a self-contained, distributable build with the GUI exe, both DLLs, the cascade, and the OpenCvSharp natives. Click **Install** in the GUI (elevates via `regsvr32`) to register the virtual camera, then **Start**.

> **Note:** the driver is an in-process COM DLL, so it can't be overwritten while any app has the virtual camera loaded. If a rebuild fails with `LNK1168: cannot open justshowme_cam.dll for writing`, close apps that use the camera (Zoom, Chrome, Teams, the Camera app) and build again.

## AI Ethics & Technology Policy (from the creator, James Hansen)

As I am a Software Developer and hold an MA in Public Policy, this project was an experiment to create an example of how AI can be used to address ethical considerations in a positive way.

The focus of this project basically boils down to "consent."

While the app does store facial recognition data, it is retained and processed locally, and has the objective of a consent-positive effect, automatically excluding people from video who did not consent to being filmed.

As policymakers worldwide grapple with regulating AI systems I hope this project, in a very small way, demonstrates that privacy-preserving, user-controlled AI applications are not only possible but essential for maintaining public trust in emerging technologies.

## Special Thanks
TheDigitalArtist at Pixabay for the [User Icon Graphic](https://pixabay.com/vectors/icon-user-person-preference-choice-9798054/) that forms part of the application icon design.

## License

This project is licensed under GPL v3
