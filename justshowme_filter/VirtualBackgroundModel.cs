using System;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// Person segmentation plus the accumulated "virtual background": the scene with the
    /// person cut out, built up over time wherever the real background becomes visible.
    /// Owns the foreground mask and the background model so BlurFaceFilter can split each
    /// frame into a live foreground subject and a background plane the filters act on.
    ///
    /// The person's pixels NEVER enter the model — only the background cutout is read — so
    /// the user's face/body can't contaminate it. Pixels not yet revealed stay unknown
    /// (tracked in a separate mask) so they read as transparent and are never painted out.
    public sealed class VirtualBackgroundModel : IDisposable
    {
        private const double LearnRate = 0.1;            // how fast revealed background settles in.
        private const double SceneChangeThreshold = 30;  // mean abs grey diff (background only) ⇒ camera moved.

        private readonly PersonSegmenter _segmenter;     // null if the seg model isn't present.
        private Mat _fgMask;     // 255 = person, this frame.
        private Mat _bg;         // BGR accumulated background (unknown areas are black placeholder).
        private Mat _known;      // 255 where _bg holds real, revealed background.
        private Mat _preview;    // BGRA snapshot (alpha = _known) for the GUI.

        public VirtualBackgroundModel(PersonSegmenter segmenter) { _segmenter = segmenter; }

        public bool Available => _segmenter != null;
        public Mat ForegroundMask => _fgMask;
        public Mat Preview => _preview;

        /// Segment the frame into the foreground mask (255 = person). No-op if unavailable.
        public void UpdateMask(Mat frame)
        {
            _fgMask?.Dispose();
            _fgMask = _segmenter?.Segment(frame);
        }

        /// Drop the current mask (when isolation isn't wanted this frame).
        public void ClearMask() { _fgMask?.Dispose(); _fgMask = null; }

        /// Learn the background wherever the person isn't. Only the background cutout is
        /// ever read; newly-revealed pixels become "known". Resets on a scene change.
        public void Update(Mat frame)
        {
            if (_fgMask == null) return;
            using (var bg = new Mat())   // 255 = background cutout (no person)
            {
                Cv2.BitwiseNot(_fgMask, bg);
                bool reset = _bg == null || _bg.Size() != frame.Size() || SceneChanged(frame, bg);
                if (reset)
                {
                    _bg?.Dispose(); _known?.Dispose();
                    _bg = new Mat(frame.Size(), frame.Type(), Scalar.All(0));
                    _known = new Mat(frame.Size(), MatType.CV_8UC1, Scalar.All(0));
                    frame.CopyTo(_bg, bg);
                    bg.CopyTo(_known);                       // the seeded cutout is known
                }
                else
                {
                    using (var blended = new Mat())
                    {
                        Cv2.AddWeighted(_bg, 1.0 - LearnRate, frame, LearnRate, 0, blended);
                        blended.CopyTo(_bg, bg);
                        Cv2.BitwiseOr(_known, bg, _known);   // once revealed, stays known
                    }
                }
            }
            BuildPreview();
        }

        /// Paint the live subject (from a clean snapshot of the input) back over the
        /// processed frame, so the foreground is never blurred, erased, or frozen.
        public void CompositeForeground(Mat clean, Mat frame)
        {
            if (_fgMask != null) clean.CopyTo(frame, _fgMask);
        }

        /// Overlay the KNOWN virtual background into region r. Unknown pixels are left
        /// untouched (transparent) — a caller's blur fallback shows through there instead
        /// of a black hole. No-op until some background has been learned.
        public void FillKnownBackground(Mat frame, Rect r)
        {
            if (_bg == null || r.Width <= 0 || r.Height <= 0) return;
            using (var src = new Mat(_bg, r))
            using (var dst = new Mat(frame, r))
            using (var m = new Mat(_known, r))
                src.CopyTo(dst, m);
        }

        // BGRA preview with alpha = known, so unfilled areas are transparent in the GUI.
        private void BuildPreview()
        {
            _preview?.Dispose();
            _preview = null;
            if (_bg == null) return;
            var bgr = Cv2.Split(_bg);                        // B, G, R planes
            try
            {
                _preview = new Mat();
                Cv2.Merge(new[] { bgr[0], bgr[1], bgr[2], _known }, _preview);
            }
            finally { foreach (var c in bgr) c.Dispose(); }
        }

        // Camera-moved test: mean abs grey diff, measured ONLY where the background is both
        // visible now and already known (never over unknown black holes ⇒ no false trigger).
        private bool SceneChanged(Mat frame, Mat bgCutout)
        {
            using (var a = new Mat())
            using (var b = new Mat())
            using (var d = new Mat())
            using (var m = new Mat())
            {
                Cv2.CvtColor(frame, a, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(_bg, b, ColorConversionCodes.BGR2GRAY);
                Cv2.Absdiff(a, b, d);
                Cv2.BitwiseAnd(bgCutout, _known, m);
                return Cv2.Mean(d, m).Val0 > SceneChangeThreshold;
            }
        }

        public void Dispose()
        {
            _segmenter?.Dispose();
            _fgMask?.Dispose();
            _bg?.Dispose();
            _known?.Dispose();
            _preview?.Dispose();
        }
    }
}
