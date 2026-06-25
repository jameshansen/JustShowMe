using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// Default JustShowMe filter. Detects faces at any angle with YuNet (DNN),
    /// tracks them across frames for stability, and blurs them per the selected
    /// mode. Boxes are padded slightly so hair/ears and detector jitter are covered.
    public sealed class BlurFaceFilter : IFrameFilter
    {
        private const string YuNetModel = "face_detection_yunet_2023mar.onnx";
        private const string SFaceModel = "face_recognition_sface_2021dec.onnx";
        private const double PadFactor = 0.15;   // grow each box 15% on every side

        private readonly IFaceDetector _detector;
        private readonly SFaceRecognizer _recognizer;
        private readonly FaceTracker _tracker = new FaceTracker();

        /// Which detector is active (for diagnostics / the GUI to surface).
        public string DetectorName => _detector.Name;

        public BlurFaceFilter()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _detector = new YuNetFaceDetector(RequireModel(dir, YuNetModel));
            _recognizer = new SFaceRecognizer(RequireModel(dir, SFaceModel));
        }

        private static string RequireModel(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Model not found next to the filter DLL (expected {name}).", path);
            return path;
        }

        public IReadOnlyList<DetectedFaceInfo> Process(Mat frame, FilterSettings settings)
        {
            var faces = new List<DetectedFaceInfo>();
            if (frame == null || frame.Empty()) return faces;

            var detections = _detector.Detect(frame);
            var boxes = new List<Rect>(detections.Count);
            var embeddings = new List<float[]>(detections.Count);
            foreach (var d in detections)
            {
                boxes.Add(d.Box);
                // ponytail: SFace forward per detected face per frame. Fine for a
                // handful of faces; if CPU-bound, recognise only new/stale tracks.
                embeddings.Add(_recognizer.Embed(frame, d.Box, d.Landmarks));
            }

            foreach (var track in _tracker.Update(boxes, embeddings))
            {
                faces.Add(new DetectedFaceInfo { Id = track.Id, Box = track.Box, Embedding = track.Embedding });

                bool blur = settings.Mode == FilterMode.BlurAll
                            || !IsAllowed(track.Embedding, settings.AllowedEmbeddings);
                if (blur) BlurRegion(frame, Pad(track.Box, frame.Size()), settings.BlurStrength);
            }
            return faces;
        }

        private static bool IsAllowed(float[] embedding, List<float[]> allowed)
        {
            if (embedding == null || allowed == null) return false;
            foreach (var a in allowed)
                if (FaceMatch.IsSame(embedding, a)) return true;
            return false;
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

        public void Dispose() { _detector?.Dispose(); _recognizer?.Dispose(); }
    }
}
