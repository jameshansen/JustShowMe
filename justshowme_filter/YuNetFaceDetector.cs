using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace JustShowMe.Filter
{
    /// YuNet face detector (face_detection_yunet_2023mar.onnx) run through OpenCV's
    /// DNN module. YuNet detects faces across a wide range of yaw/pitch — profiles,
    /// tilts, look-down poses — which Haar cascades miss.
    ///
    /// OpenCvSharp 4.11 does NOT ship the high-level cv::FaceDetectorYN wrapper, so
    /// this class IS that wrapper: it loads the ONNX net, runs one forward pass, and
    /// reproduces FaceDetectorYN's post-processing:
    ///   - priors on 3 feature maps at strides 8 / 16 / 32
    ///   - score = sqrt(classification * objectness)
    ///   - box decode: cx=(col+dx)*s, cy=(row+dy)*s, w=exp(dw)*s, h=exp(dh)*s
    ///   - non-maximum suppression
    /// (Landmark outputs exist but aren't needed for blurring, so they're ignored.)
    internal sealed class YuNetFaceDetector : IFaceDetector
    {
        private static readonly int[] Strides = { 8, 16, 32 };

        // Output blob names, in the order cv::FaceDetectorYN uses them.
        private static readonly string[] OutNames =
        {
            "cls_8", "cls_16", "cls_32",
            "obj_8", "obj_16", "obj_32",
            "bbox_8", "bbox_16", "bbox_32",
            "kps_8", "kps_16", "kps_32",
        };

        private readonly Net _net;
        private readonly float _scoreThreshold;
        private readonly float _nmsThreshold;
        private readonly int _longSide;

        public string Name => "YuNet (DNN, any-angle)";

        /// <param name="longSide">Detection input long-side in pixels (rounded to a
        /// multiple of 32). Smaller = faster, larger = better on small faces.</param>
        public YuNetFaceDetector(string onnxPath, float scoreThreshold = 0.6f,
                                 float nmsThreshold = 0.3f, int longSide = 640)
        {
            _net = CvDnn.ReadNetFromOnnx(onnxPath);
            if (_net == null || _net.Empty())
                throw new InvalidOperationException("Failed to load YuNet ONNX model: " + onnxPath);
            _scoreThreshold = scoreThreshold;
            _nmsThreshold = nmsThreshold;
            _longSide = longSide;
        }

        public IReadOnlyList<FaceDetection> Detect(Mat bgr)
        {
            var result = new List<FaceDetection>();
            if (bgr == null || bgr.Empty()) return result;

            // Pick an input size that preserves aspect and is a multiple of 32
            // (the largest stride), which YuNet requires.
            int ow = bgr.Width, oh = bgr.Height;
            double scale = (double)_longSide / Math.Max(ow, oh);
            int iw = Math.Max(32, (int)Math.Round(ow * scale / 32.0) * 32);
            int ih = Math.Max(32, (int)Math.Round(oh * scale / 32.0) * 32);

            var outs = new Mat[OutNames.Length];
            for (int i = 0; i < outs.Length; i++) outs[i] = new Mat();

            using (var blob = CvDnn.BlobFromImage(bgr, 1.0, new Size(iw, ih),
                                                  new Scalar(0, 0, 0), false, false))
            {
                _net.SetInput(blob);
                try
                {
                    _net.Forward(outs, OutNames);

                    var boxes = new List<Rect>();
                    var scores = new List<float>();
                    var landmarks = new List<Point2f[]>();
                    double sx = (double)ow / iw, sy = (double)oh / ih;

                    for (int si = 0; si < Strides.Length; si++)
                    {
                        int s = Strides[si];
                        int cols = iw / s, rows = ih / s;
                        float[] cls = ToFloats(outs[si]);          // cls_*
                        float[] obj = ToFloats(outs[3 + si]);      // obj_*
                        float[] bbox = ToFloats(outs[6 + si]);     // bbox_* (4 per prior)
                        float[] kps = ToFloats(outs[9 + si]);      // kps_*  (10 per prior)

                        int n = rows * cols;
                        for (int idx = 0; idx < n; idx++)
                        {
                            float c = Clamp01(cls[idx]);
                            float o = Clamp01(obj[idx]);
                            float score = (float)Math.Sqrt(c * o);
                            if (score < _scoreThreshold) continue;

                            int row = idx / cols, col = idx % cols;
                            float dx = bbox[idx * 4 + 0];
                            float dy = bbox[idx * 4 + 1];
                            float dw = bbox[idx * 4 + 2];
                            float dh = bbox[idx * 4 + 3];

                            float cx = (col + dx) * s;
                            float cy = (row + dy) * s;
                            float w = (float)Math.Exp(dw) * s;
                            float h = (float)Math.Exp(dh) * s;

                            // Map from blob coords back to the original frame.
                            int x1 = (int)Math.Round((cx - w / 2) * sx);
                            int y1 = (int)Math.Round((cy - h / 2) * sy);
                            int bw = (int)Math.Round(w * sx);
                            int bh = (int)Math.Round(h * sy);
                            boxes.Add(new Rect(x1, y1, bw, bh));
                            scores.Add(score);

                            // Same decode for the 5 landmarks: (col+kx)*s, (row+ky)*s.
                            var lm = new Point2f[5];
                            for (int j = 0; j < 5; j++)
                                lm[j] = new Point2f(
                                    (float)((col + kps[idx * 10 + 2 * j]) * s * sx),
                                    (float)((row + kps[idx * 10 + 2 * j + 1]) * s * sy));
                            landmarks.Add(lm);
                        }
                    }

                    if (boxes.Count == 0) return result;

                    CvDnn.NMSBoxes(boxes, scores, _scoreThreshold, _nmsThreshold,
                                   out int[] keep, 1.0f, 0);
                    foreach (int k in keep)
                        result.Add(new FaceDetection
                        {
                            Box = boxes[k], Score = scores[k], Landmarks = landmarks[k],
                        });
                }
                finally
                {
                    foreach (var m in outs) m.Dispose();
                }
            }
            return result;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// Flattens a CV_32F output Mat to a float[] regardless of its dim layout.
        private static float[] ToFloats(Mat m)
        {
            Mat src = m.IsContinuous() ? m : m.Clone();
            int n = (int)src.Total() * src.Channels();
            var arr = new float[n];
            Marshal.Copy(src.Data, arr, 0, n);
            if (!ReferenceEquals(src, m)) src.Dispose();
            return arr;
        }

        public void Dispose() => _net?.Dispose();
    }
}
