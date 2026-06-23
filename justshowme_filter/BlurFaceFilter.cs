using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// Default JustShowMe filter. Detects faces (YuNet DNN if its model is present
    /// next to this DLL, otherwise a Haar cascade), tracks them across frames for
    /// stability, and blurs them per the selected mode. Boxes are padded slightly so
    /// hair/ears and detector jitter are covered.
    public sealed class BlurFaceFilter : IFrameFilter
    {
        private const string YuNetModel = "face_detection_yunet_2023mar.onnx";
        private const string HaarModel = "haarcascade_frontalface_alt.xml";
        private const double PadFactor = 0.15;   // grow each box 15% on every side

        private readonly IFaceDetector _detector;
        private readonly FaceTracker _tracker = new FaceTracker();

        /// Which detector ended up active (for diagnostics / the GUI to surface).
        public string DetectorName => _detector.Name;

        public BlurFaceFilter()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string onnx = Path.Combine(dir, YuNetModel);
            string cascade = Path.Combine(dir, HaarModel);

            if (File.Exists(onnx)) _detector = new YuNetFaceDetector(onnx);
            else if (File.Exists(cascade)) _detector = new HaarFaceDetector(cascade);
            else throw new FileNotFoundException(
                $"No face model found next to the filter DLL (expected {YuNetModel} or {HaarModel}).", onnx);
        }

        public IReadOnlyList<DetectedFaceInfo> Process(Mat frame, FilterSettings settings)
        {
            var faces = new List<DetectedFaceInfo>();
            if (frame == null || frame.Empty()) return faces;

            var detections = _detector.Detect(frame);
            var boxes = new List<Rect>(detections.Count);
            foreach (var d in detections) boxes.Add(d.Box);

            foreach (var track in _tracker.Update(boxes))
            {
                int id = track.Key;
                Rect box = track.Value;
                faces.Add(new DetectedFaceInfo { Id = id, Box = box });

                bool blur = settings.Mode == FilterMode.BlurAll
                            || !settings.AllowedFaceIds.Contains(id);
                if (blur) BlurRegion(frame, Pad(box, frame.Size()), settings.BlurStrength);
            }
            return faces;
        }

        private static Rect Pad(Rect r, Size bounds)
        {
            int px = (int)(r.Width * PadFactor), py = (int)(r.Height * PadFactor);
            int x = r.X - px, y = r.Y - py, w = r.Width + 2 * px, h = r.Height + 2 * py;
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > bounds.Width) w = bounds.Width - x;
            if (y + h > bounds.Height) h = bounds.Height - y;
            return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
        }

        private static void BlurRegion(Mat frame, Rect r, int strength)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            int k = strength | 1; // Gaussian kernel must be odd.
            using (var region = new Mat(frame, r))
                Cv2.GaussianBlur(region, region, new Size(k, k), 0);
        }

        public void Dispose() => _detector?.Dispose();
    }
}
