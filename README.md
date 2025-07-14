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

## AI Ethics & Technology Policy (from the creator, James Hansen)

As I am a Software Developer and Public Policy student, this project was an experiment to create an example of how AI can be used to address ethical considerations in a positive way. Unlike surveillance-oriented applications, this project demonstrates algorithmic accountability. The architecture embodies privacy-by-design principles through local processing.

There is a clear consent mechanism, and the face detection process is clear with the source code available here. This project aligns with emerging AI governance frameworks like the EU AI Act's requirements for high-risk AI systems.

From a technology policy perspective, this represents a model for how AI tools can enhance individual agency rather than diminish it. Beyond corporate and social use, it also could enhance democratic participation in digital spaces while maintaining human dignity and privacy rights.

As policymakers worldwide grapple with regulating AI systems I hope this project, in a very small way, demonstrates that privacy-preserving, user-controlled AI applications are not only possible but essential for maintaining public trust in emerging technologies.

## License

This project is licensed under Apache License v2.0
