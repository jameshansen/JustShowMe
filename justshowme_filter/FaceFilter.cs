using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// Default JustShowMe filter: Haar-cascade face detection + selective Gaussian
    /// blur. Faces get a stable id (cosine match on a histogram embedding) so the
    /// GUI can remember which ones are allowed.
    public sealed class FaceFilter : IFrameFilter
    {
        private readonly CascadeClassifier _cascade;
        private readonly Dictionary<int, float[]> _knownFaces = new Dictionary<int, float[]>();
        private int _nextFaceId = 1;

        public FaceFilter()
        {
            // Load the cascade from beside this DLL, not the host's working dir.
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            string path = Path.Combine(dir, "haarcascade_frontalface_alt.xml");
            if (!File.Exists(path))
                throw new FileNotFoundException("Cascade file missing next to the filter DLL.", path);
            _cascade = new CascadeClassifier(path);
        }

        public IReadOnlyList<DetectedFaceInfo> Process(Mat frame, FilterSettings settings)
        {
            var found = new List<DetectedFaceInfo>();
            if (frame == null || frame.Empty()) return found;

            using (var gray = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                foreach (var rect in _cascade.DetectMultiScale(gray, 1.3, 5))
                {
                    int id;
                    using (var faceRegion = new Mat(gray, rect))
                        id = MatchOrCreateFace(faceRegion);

                    found.Add(new DetectedFaceInfo { Id = id, Box = rect });

                    bool blur = settings.Mode == FilterMode.BlurAll
                        || !settings.AllowedFaceIds.Contains(id);
                    if (blur) BlurRegion(frame, rect, settings.BlurStrength);
                }
            }
            return found;
        }

        private static void BlurRegion(Mat frame, Rect r, int strength)
        {
            var clamped = new Rect(
                Math.Max(0, r.X),
                Math.Max(0, r.Y),
                Math.Min(r.Width, frame.Width - r.X),
                Math.Min(r.Height, frame.Height - r.Y));
            if (clamped.Width <= 0 || clamped.Height <= 0) return;

            int k = strength | 1; // Gaussian kernel must be odd.
            using (var region = new Mat(frame, clamped))
                Cv2.GaussianBlur(region, region, new Size(k, k), 0);
        }

        private int MatchOrCreateFace(Mat faceRegion)
        {
            float[] embedding = Embed(faceRegion);
            int bestId = -1;
            double best = 0.8; // similarity threshold
            foreach (var kvp in _knownFaces)
            {
                double s = Cosine(embedding, kvp.Value);
                if (s > best) { best = s; bestId = kvp.Key; }
            }
            if (bestId != -1) return bestId;

            int id = _nextFaceId++;
            _knownFaces[id] = embedding;
            return id;
        }

        private static float[] Embed(Mat faceRegion)
        {
            using (var resized = new Mat())
            using (var hist = new Mat())
            using (var noMask = new Mat())
            {
                Cv2.Resize(faceRegion, resized, new Size(128, 128));
                Cv2.CalcHist(new[] { resized }, new[] { 0 }, noMask, hist, 1,
                    new[] { 256 }, new[] { new Rangef(0, 256) });

                var e = new float[128];
                for (int i = 0; i < Math.Min(128, hist.Rows); i++)
                    e[i] = hist.At<float>(i, 0);
                return e;
            }
        }

        private static double Cosine(float[] a, float[] b)
        {
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            double denom = Math.Sqrt(na) * Math.Sqrt(nb);
            return denom == 0 ? 0 : dot / denom;
        }

        public void Dispose() => _cascade?.Dispose();
    }
}
