# JustShowMe
Webcam Privacy Filter
<img width="1549" height="1037" alt="image" src="https://github.com/user-attachments/assets/49395be6-acf0-4a38-9b5c-e959fc9add67" />

## Concept
With remote work becoming increasingly common, working and appearing on camera in video meetings in shared spaces, from a coffee shop to a living room is now normal.

From an ethics and technology policy standpoint, this creates a problem, as people may appear on video who have not consented as such, even more problematic if the meeting is being recorded.

While blurring the entire background is an option, this can sometimes create strange effects make your video feed less visually appealing.

"JustShowMe" provides the solution. Using OpenCV, other faces are identified when they appear and blurred selectively, allowing you to show everyone your beautiful home decord, or cool coffee shop you happen to be at, without impacting individuals privacy.

## Implementation

The project is a Visual Studio 2019 solution, written in C# using the OpenCV libraries and haarcascades object detection algorithms for faces.

To relay the processed video to another application, a virtual "JustShowMe Cam" is initialized, which is taken from https://github.com/tshino/softcam (MIT Licensed)

## AI Ethics & Technology Policy (from the creator, James Hansen)

As I am a Software Developer and Public Policy student, this project was an experiment to create an example of how AI can be used to address ethical considerations in a positive way.

The focus of this project basically boils down to "consent."

Ethics and consent has been a hot-button topic in many AI-based platforms and projects, whether it is:
* consenting to your data being collected and used in facial recognition systems
* whether work rights-holders have published being used to train models.
* to even whether residents want an "AI Datacentre" in their town or city.

This project grapples with the ethics surrounding AI-tools. The app does store facial recognition data, it is retained and processed locally, and has the objective of a consent-positive effect, automatically excluding people from video who did not consent to being filmed.

Does this neutralize the ethical implications? I think in this case, it balances out.

As policymakers worldwide grapple with regulating AI systems I hope this project, in a very small way, demonstrates that privacy-preserving, user-controlled AI applications are not only possible but essential for maintaining public trust in emerging technologies.

## License

This project is licensed under Apache License v2.0
