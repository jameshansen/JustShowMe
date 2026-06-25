using System;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace JustShowMe.Filter
{
    /// SFace face recognizer (face_recognition_sface_2021dec.onnx), the recognition
    /// model OpenCV ships alongside YuNet — they're a matched pair in the OpenCV Model
    /// Zoo: YuNet's 5 landmarks are exactly what SFace needs to align a face, so the
    /// two compose cleanly. Turns a face crop into a 128-D embedding; the same person
    /// across angles/lighting lands near the same point, so cosine similarity tells us
    /// "is this the face the user allowed?" — identity that survives the ephemeral,
    /// position-based track ids.
    ///
    /// As with YuNetFaceDetector, OpenCvSharp 4.11 doesn't expose the high-level
    /// cv::FaceRecognizerSF, so this reproduces it: align-crop to the canonical
    /// 112x112 template, one forward pass, compare by cosine.
    internal sealed class SFaceRecognizer : IDisposable
    {
        // Canonical 5-point template SFace was trained against (right eye, left eye,
        // nose, right & left mouth corner) on a 112x112 crop. Same as ArcFace.
        private static readonly Point2f[] Template =
        {
            new Point2f(38.2946f, 51.6963f),
            new Point2f(73.5318f, 51.5014f),
            new Point2f(56.0252f, 71.7366f),
            new Point2f(41.5493f, 92.3655f),
            new Point2f(70.7299f, 92.2041f),
        };

        private readonly Net _net;

        public SFaceRecognizer(string onnxPath)
        {
            _net = CvDnn.ReadNetFromOnnx(onnxPath);
            if (_net == null || _net.Empty())
                throw new InvalidOperationException("Failed to load SFace ONNX model: " + onnxPath);
        }

        /// Aligns the face using its landmarks (falls back to a plain box crop if
        /// landmarks are missing or alignment fails) and returns its 128-D embedding.
        public float[] Embed(Mat bgr, Rect box, Point2f[] landmarks)
        {
            using (var aligned = AlignCrop(bgr, box, landmarks))
            {
                if (aligned == null || aligned.Empty()) return null;
                using (var blob = CvDnn.BlobFromImage(aligned, 1.0, new Size(112, 112),
                                                      new Scalar(0, 0, 0), false, false))
                {
                    _net.SetInput(blob);
                    using (var outMat = _net.Forward())
                        return ToFloats(outMat);
                }
            }
        }

        private static Mat AlignCrop(Mat bgr, Rect box, Point2f[] landmarks)
        {
            if (landmarks != null && landmarks.Length == 5)
            {
                // Closed-form 2D similarity (Procrustes) fitting all 5 landmarks onto
                // the template. Deterministic — unlike estimateAffinePartial2D, whose
                // RANSAC made the alignment (and thus the embedding) jitter frame to
                // frame, which is what let one person's face match another's.
                double msx = 0, msy = 0, mtx = 0, mty = 0;
                for (int i = 0; i < 5; i++)
                {
                    msx += landmarks[i].X; msy += landmarks[i].Y;
                    mtx += Template[i].X;  mty += Template[i].Y;
                }
                msx /= 5; msy /= 5; mtx /= 5; mty /= 5;

                double dot = 0, cross = 0, denom = 0;
                for (int i = 0; i < 5; i++)
                {
                    double ax = landmarks[i].X - msx, ay = landmarks[i].Y - msy;
                    double bx = Template[i].X - mtx, by = Template[i].Y - mty;
                    dot += ax * bx + ay * by;
                    cross += ax * by - ay * bx;
                    denom += ax * ax + ay * ay;
                }
                if (denom > 1e-6)
                {
                    double p = dot / denom, q = cross / denom;   // p = s·cosθ, q = s·sinθ
                    double tx = mtx - (p * msx - q * msy);
                    double ty = mty - (q * msx + p * msy);
                    using (var m = new Mat(2, 3, MatType.CV_64FC1))
                    {
                        m.Set<double>(0, 0, p); m.Set<double>(0, 1, -q); m.Set<double>(0, 2, tx);
                        m.Set<double>(1, 0, q); m.Set<double>(1, 1, p);  m.Set<double>(1, 2, ty);
                        var dst = new Mat();
                        Cv2.WarpAffine(bgr, dst, m, new Size(112, 112));
                        return dst;
                    }
                }
            }

            // Fallback: clamp the box to the frame and resize. Less accurate (no
            // alignment) but only ever makes the same face look *less* alike, which
            // errs toward blurring — the safe direction for a privacy tool.
            var c = new Rect(
                Math.Max(0, box.X), Math.Max(0, box.Y),
                Math.Min(box.Width, bgr.Width - Math.Max(0, box.X)),
                Math.Min(box.Height, bgr.Height - Math.Max(0, box.Y)));
            if (c.Width <= 0 || c.Height <= 0) return null;
            var resized = new Mat();
            using (var crop = new Mat(bgr, c))
                Cv2.Resize(crop, resized, new Size(112, 112));
            return resized;
        }

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
