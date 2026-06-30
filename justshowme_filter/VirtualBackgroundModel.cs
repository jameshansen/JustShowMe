using System;
using System.Collections.Generic;
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
        private const int FeatherKernel = 15;            // odd; softens the foreground cut-out edge.

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
        /// pad > 0 dilates the mask so the kept foreground area grows by that many pixels.
        public void UpdateMask(Mat frame, int pad)
        {
            _fgMask?.Dispose();
            _fgMask = _segmenter?.Segment(frame);
            if (_fgMask != null && pad > 0)
                using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(pad * 2 + 1, pad * 2 + 1)))
                    Cv2.Dilate(_fgMask, _fgMask, k);
        }

        /// Drop the current mask (when isolation isn't wanted this frame).
        public void ClearMask() { _fgMask?.Dispose(); _fgMask = null; }

        /// A copy of the frame with the foreground subject blacked out, so face detection
        /// only ever sees the background — the webcam user is never detected. Caller disposes.
        public Mat BackgroundCutout(Mat frame)
        {
            var cut = frame.Clone();
            if (_fgMask != null) cut.SetTo(Scalar.All(0), _fgMask);
            return cut;
        }

        /// Learn the background wherever there's no person: not the segmented subject, and
        /// not any detected background person (so they never bake in). Only that cutout is
        /// ever read; newly-revealed pixels become "known". Resets on a scene change.
        public void Update(Mat frame, IReadOnlyList<Rect> people)
        {
            if (_fgMask == null) return;
            using (var bg = new Mat())   // 255 = background cutout (no person)
            {
                Cv2.BitwiseNot(_fgMask, bg);
                if (people != null)
                    foreach (var r in people)
                        if (r.Width > 0 && r.Height > 0)
                            Cv2.Rectangle(bg, r, Scalar.All(0), -1);   // -1 = filled
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
                    // Freshly-revealed background (visible now, not yet known) must be SEEDED
                    // at full value — blending it up from black would copy near-black to the
                    // output for many frames. Only already-known background gets smoothed.
                    using (var blended = new Mat())
                    using (var knownNow = new Mat())   // bg AND known: smooth it
                    using (var fresh = new Mat())      // bg AND NOT known: seed at full value
                    {
                        Cv2.BitwiseAnd(bg, _known, knownNow);
                        Cv2.BitwiseNot(_known, fresh);
                        Cv2.BitwiseAnd(fresh, bg, fresh);

                        Cv2.AddWeighted(_bg, 1.0 - LearnRate, frame, LearnRate, 0, blended);
                        blended.CopyTo(_bg, knownNow);
                        frame.CopyTo(_bg, fresh);
                        Cv2.BitwiseOr(_known, bg, _known);   // once revealed, stays known
                    }
                }
            }
            BuildPreview();
        }

        /// Paint the live subject (from a clean snapshot of the input) back over the
        /// processed frame, so the foreground is never blurred, erased, or frozen. The mask
        /// edge is feathered (soft alpha) so the cut-out blends instead of showing a hard,
        /// jagged outline: out = frame*(1-a) + clean*a, with a = blurred mask in [0,1].
        public void CompositeForeground(Mat clean, Mat frame)
        {
            if (_fgMask == null) return;
            using (var alpha = new Mat())
            using (var inv = new Mat())
            using (var af = new Mat())
            using (var invf = new Mat())
            using (var cf = new Mat())
            using (var ff = new Mat())
            {
                Cv2.GaussianBlur(_fgMask, alpha, new Size(FeatherKernel, FeatherKernel), 0);
                Cv2.BitwiseNot(alpha, inv);                               // 255 - alpha, so af + invf = 1

                Cv2.CvtColor(alpha, af, ColorConversionCodes.GRAY2BGR);
                Cv2.CvtColor(inv, invf, ColorConversionCodes.GRAY2BGR);
                af.ConvertTo(af, MatType.CV_32FC3, 1.0 / 255.0);
                invf.ConvertTo(invf, MatType.CV_32FC3, 1.0 / 255.0);
                clean.ConvertTo(cf, MatType.CV_32FC3);
                frame.ConvertTo(ff, MatType.CV_32FC3);

                Cv2.Multiply(cf, af, cf);
                Cv2.Multiply(ff, invf, ff);
                Cv2.Add(cf, ff, ff);
                ff.ConvertTo(frame, frame.Type());
            }
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
