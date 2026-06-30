using System;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace JustShowMe.Filter
{
    /// Person/background segmentation — the "Zoom virtual background" style isolation
    /// of a person sitting in front of the camera. Uses PP-HumanSeg from the OpenCV
    /// Zoo through the OpenCvSharp DNN module (the same runtime YuNet/SFace already
    /// use, so no new dependency). Returns a full-frame 8-bit mask: 255 where it
    /// thinks a person is, 0 for background.
    public sealed class PersonSegmenter : IDisposable
    {
        // PP-HumanSeg fixed input; output is planar NCHW [1,2,192,192] softmax class
        // scores (channel 1 = person). Preproc matches the OpenCV Zoo demo: RGB, (x/255-0.5)/0.5.
        private const int InW = 192, InH = 192;

        private readonly Net _net;

        public PersonSegmenter(string modelPath)
        {
            _net = CvDnn.ReadNetFromOnnx(modelPath);
            _net.SetPreferableBackend(Backend.OPENCV);
            _net.SetPreferableTarget(Target.CPU);
        }

        /// 8-bit single-channel mask at the frame's size: 255 = person, 0 = background.
        public Mat Segment(Mat frameBgr)
        {
            // (pixel - 127.5) * (1/127.5) == (pixel/255 - 0.5)/0.5, RGB order.
            using (var blob = CvDnn.BlobFromImage(
                       frameBgr, 1.0 / 127.5, new Size(InW, InH),
                       new Scalar(127.5, 127.5, 127.5), swapRB: true, crop: false))
            {
                _net.SetInput(blob);
                using (var outp = _net.Forward())
                using (var small = MaskFromOutput(outp))
                {
                    var mask = new Mat();
                    Cv2.Resize(small, mask, frameBgr.Size(), 0, 0, InterpolationFlags.Linear);
                    Cv2.Threshold(mask, mask, 127, 255, ThresholdTypes.Binary);
                    return mask;
                }
            }
        }

        // Argmax over the 2 channels of the output → person mask. The output is a
        // contiguous 4-D [1,2,192,192] tensor of softmax scores in PLANAR NCHW order:
        // the whole bg plane (192*192 floats) followed by the whole person plane. So
        // class c at pixel i lives at data[c*plane + i], not interleaved per pixel.
        // Copy it out by raw size (N-D Mats don't reshape cleanly via GetArray).
        // ponytail: plain loop over 192*192 px is trivially cheap and obviously correct.
        private static Mat MaskFromOutput(Mat outp)
        {
            int plane = InW * InH;                      // 192*192 per class
            var data = new float[plane * 2];
            System.Runtime.InteropServices.Marshal.Copy(outp.Data, data, 0, plane * 2);
            var m = new byte[plane];
            for (int i = 0; i < plane; i++)
                m[i] = data[plane + i] > data[i] ? (byte)255 : (byte)0;   // person plane vs bg plane
            using (var tmp = Mat.FromPixelData(InH, InW, MatType.CV_8UC1, m))
                return tmp.Clone();                     // own the pixels (m is transient)
        }

        public void Dispose() => _net?.Dispose();
    }
}
