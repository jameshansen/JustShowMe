# JustShowMe
Webcam Privacy Filter

## Concept
With remote work becoming increasingly common, working and appearing on camera in video meetings in shared spaces, from a coffee shop to a living room is now normal.

From an ethics and technology policy standpoint, this creates a problem, as people may appear on video who have not consented as such, even more problematic if the meeting is being recorded.

While blurring the entire background is an option, this can sometimes create strange effects make your video feed less visually appealing.

"JustShowMe" provides the solution. Using OpenCV, other faces are identified when they appear and blurred selectively, allowing you to show everyone your beautiful home decord, or cool coffee shop you happen to be at, without impacting individuals privacy.

## Implementation

The project is a Visual Studio 2019 solution, written in C# using the OpenCV libraries and haarcascades object detection algorithms for faces.

To relay the processed video to another application, a virtual "JustShowMe Cam" is initialized, which is taken from https://github.com/tshino/softcam (MIT Licensed)

## License

This project is licensed under Apache License v2.0
