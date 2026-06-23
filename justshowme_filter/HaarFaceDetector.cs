using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// Fallback detector: Haar cascade (frontal faces only). Used when the YuNet
    /// ONNX model isn't present next to the filter DLL.
    internal sealed class HaarFaceDetector : IFaceDetector
    {
        private readonly CascadeClassifier _cascade;

        public string Name => "Haar cascade (frontal only)";

        public HaarFaceDetector(string cascadePath)
        {
            _cascade = new CascadeClassifier(cascadePath);
        }

        public IReadOnlyList<FaceDetection> Detect(Mat bgr)
        {
            var result = new List<FaceDetection>();
            if (bgr == null || bgr.Empty()) return result;
            using (var gray = new Mat())
            {
                Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
                foreach (var r in _cascade.DetectMultiScale(gray, 1.3, 5))
                    result.Add(new FaceDetection { Box = r, Score = 1f });
            }
            return result;
        }

        public void Dispose() => _cascade?.Dispose();
    }
}
