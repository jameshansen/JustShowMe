using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// Default JustShowMe filter. Detects faces at any angle with YuNet (DNN),
    /// recognises them (SFace) so allowed people stay clear, tracks them across
    /// frames for stability, and blurs each person per the selected mode and scope —
    /// just the face, or the whole body (a region anchored on the face).
    public sealed class BlurFaceFilter : IFrameFilter
    {
        private const string YuNetModel = "face_detection_yunet_2023mar.onnx";
        private const string SFaceModel = "face_recognition_sface_2021dec.onnx";
        private const string SegModel = "human_segmentation_pphumanseg_2023mar.onnx";
        private const double PadFactor = 0.15;   // grow each face box 15% on every side

        // Cap on how far back the background history is kept (bounds memory; the GUI
        // slider can't exceed this). ~6s at 30fps ≈ 180 full frames.
        private const double MaxHistorySeconds = 6.0;

        private readonly IFaceDetector _detector;
        private readonly SFaceRecognizer _recognizer;
        private readonly VirtualBackgroundModel _vbm;  // segmentation + virtual background.
        private readonly FaceTracker _tracker = new FaceTracker();

        // Foreground mask (255 = subject) and the virtual-background preview, surfaced for
        // the GUI. Owned by the model; read on the pump thread right after Process.
        public Mat ForegroundMask => _vbm.ForegroundMask;
        public Mat VirtualBackground => _vbm.Preview;

        // Rolling background plate for Smart Fill: clean input frames, timestamped.
        // Recorded continuously (every mode), so switching INTO Smart Fill has a plate
        // ready immediately instead of blurring until the buffer fills.
        // ponytail: ~15fps store throttle keeps the ~6s buffer near ~80MB, not ~160MB.
        private const long StoreIntervalMs = 66;   // ~15fps history granularity
        private readonly List<KeyValuePair<long, Mat>> _history = new List<KeyValuePair<long, Mat>>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// Which detector is active (for diagnostics / the GUI to surface).
        public string DetectorName => _detector.Name;

        public BlurFaceFilter()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _detector = new YuNetFaceDetector(RequireModel(dir, YuNetModel));
            _recognizer = new SFaceRecognizer(RequireModel(dir, SFaceModel));
            // Segmentation is optional: if the model isn't bundled, the virtual-background
            // feature just stays off (no hard failure).
            PersonSegmenter segmenter = null;
            string seg = Path.Combine(dir, SegModel);
            try { if (File.Exists(seg)) segmenter = new PersonSegmenter(seg); }
            catch (Exception ex) { Debug.WriteLine("PersonSegmenter load failed: " + ex.Message); }
            _vbm = new VirtualBackgroundModel(segmenter);
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

            bool vbMode = settings.PersonMode == PersonMode.VirtualBackground;
            bool smartFill = settings.PersonMode == PersonMode.SmartFill;
            bool vbFill = smartFill && settings.SmartFillMode == SmartFillMode.VirtualBackground;

            // Person segmentation FIRST — it drives the foreground/background split and
            // decides where we run face detection. Computed when isolation is on (or a
            // virtual-background fill wants it).
            if ((settings.ForegroundMaskEnabled || vbFill || vbMode) && _vbm.Available) _vbm.UpdateMask(frame, settings.ForegroundPad);
            else _vbm.ClearMask();

            // Isolation splits the frame into a BACKGROUND plane that all the filters act on
            // and a FOREGROUND plane (the live subject) composited back on top at the end.
            // Snapshot the live subject source now, before anything mutates the frame.
            bool isolate = settings.ForegroundMaskEnabled && _vbm.ForegroundMask != null;
            Mat clean = isolate ? frame.Clone() : null;

            // Face detection. When isolating, run it on the background cutout (the subject
            // blacked out) so the webcam user is NEVER detected — only background people are,
            // so the user can't be blurred or erased. Embeddings still come from the real
            // frame (the detected faces are visible there).
            Mat detectInput = isolate ? _vbm.BackgroundCutout(frame) : frame;
            var detections = _detector.Detect(detectInput);
            if (isolate) detectInput.Dispose();
            var boxes = new List<Rect>(detections.Count);
            var embeddings = new List<float[]>(detections.Count);
            foreach (var d in detections)
            {
                boxes.Add(d.Box);
                // ponytail: SFace forward per detected face per frame. Fine for a
                // handful of faces; if CPU-bound, recognise only new/stale tracks.
                embeddings.Add(_recognizer.Embed(frame, d.Box, d.Landmarks));
            }

            // Rewind plate: only the Rewind submode needs the rolling clean-frame buffer, so
            // record (and keep it in memory) only then; otherwise free it. ponytail: the
            // always-on buffer wasted ~80MB in modes that never read it.
            bool rewindMode = smartFill && settings.SmartFillMode == SmartFillMode.Rewind;
            long now = _clock.ElapsedMilliseconds;
            if (rewindMode) PushHistory(frame, now);
            else if (_history.Count > 0) ClearHistory();
            Mat rewind = rewindMode ? GetHistoryFrame(now, settings.SmartFillSeconds) : null;

            _tracker.MaxAge = Math.Max(0, settings.GhostSustainFrames);

            // Every tracked person here is a BACKGROUND person (the subject was masked out of
            // detection). Their bodies are the exclusion/replacement array: kept out of the
            // virtual background, and replaced by it in the output. Tracking persists them
            // across dropped detections (GhostSustainFrames).
            var toObscure = new List<Rect>();
            var safeZones = new List<Rect>();
            var people = new List<Rect>();
            foreach (var track in _tracker.Update(boxes, embeddings))
            {
                faces.Add(new DetectedFaceInfo { Id = track.Id, Box = track.Box, Embedding = track.Embedding, Seen = track.Seen });

                Rect body = BodyRegion(track.Box, frame.Size(), settings.BodyScale);
                people.Add(body);

                Rect region = settings.PersonMode == PersonMode.BlurFace ? Pad(track.Box, frame.Size()) : body;
                bool obscure = settings.Mode == FilterMode.BlurAll
                               || !IsAllowed(track.Embedding, settings.AllowedEmbeddings);
                (obscure ? toObscure : safeZones).Add(region);
            }

            // Build the virtual background whenever isolation is on: it learns the real scene
            // only where there's no person — neither the segmented subject nor a detected
            // background person — so nobody bakes in.
            if (isolate) _vbm.Update(frame, people);

            if (vbMode)
            {
                // Virtual Background mode: no per-person work. Replace the whole background
                // with the known virtual background (people-free); unrevealed areas keep the
                // live frame. The subject is excluded from "known" so it isn't touched, and
                // the live foreground is composited on top below.
                if (isolate) _vbm.FillKnownBackground(frame, new Rect(0, 0, frame.Width, frame.Height));
            }
            else
            {
                // Keep allowed people clear even when a neighbour's (larger) region overlaps
                // them: snapshot their original pixels, obscure everyone else, then paint the
                // snapshots back. ponytail: rectangles overlap imperfectly — a non-allowed
                // person directly behind an allowed one shows through their safe zone. True
                // per-pixel masks are the segmentation upgrade.
                var saved = new List<KeyValuePair<Rect, Mat>>(safeZones.Count);
                foreach (var r in safeZones)
                    if (r.Width > 0 && r.Height > 0)
                        saved.Add(new KeyValuePair<Rect, Mat>(r, new Mat(frame, r).Clone()));

                foreach (var r in toObscure)
                {
                    // Virtual Background fill: blur the region, then overlay the KNOWN
                    // background on top — where we've learned the real background it shows
                    // through cleanly, where we haven't the blur covers it (never a black
                    // hole). Rewind: copy from a recent frame. Subject composited on top after.
                    if (vbFill)
                    {
                        BlurRegion(frame, r, settings.BlurStrength);
                        _vbm.FillKnownBackground(frame, r);
                    }
                    else if (rewindMode && rewind != null)
                        FillFrom(rewind, frame, r);
                    else
                        BlurRegion(frame, r, settings.BlurStrength);
                }

                foreach (var kv in saved)
                {
                    using (var dst = new Mat(frame, kv.Key)) kv.Value.CopyTo(dst);
                    kv.Value.Dispose();
                }
            }

            // Composite the live foreground subject back on top of the processed background
            // plane, so they're never blurred, erased, or frozen.
            if (isolate)
            {
                _vbm.CompositeForeground(clean, frame);
                clean.Dispose();
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

        // A whole-person region anchored on the face: we can only *identify* the face,
        // so the body is estimated from it — widthFactor face-widths wide (GUI slider),
        // from half a face-height above the head straight down to the bottom of the
        // frame. Clamped to the frame. ponytail: a rectangle, not a body model —
        // over-blur is the safe error for privacy; segmentation is the phase-2 upgrade.
        private static Rect BodyRegion(Rect face, Size bounds, double widthFactor)
        {
            int w = (int)(face.Width * widthFactor);
            int x = face.X + face.Width / 2 - w / 2;
            int top = face.Y - face.Height;          // a full face-height above the head
            if (x < 0) { w += x; x = 0; }
            if (top < 0) top = 0;
            if (x + w > bounds.Width) w = bounds.Width - x;
            int h = bounds.Height - top;             // down to the frame bottom
            return new Rect(x, top, Math.Max(0, w), Math.Max(0, h));
        }

        private static void BlurRegion(Mat frame, Rect r, int strength)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            int k = strength | 1; // Gaussian kernel must be odd.
            using (var region = new Mat(frame, r))
                Cv2.GaussianBlur(region, region, new Size(k, k), 0);
        }

        // Copy a region from the rewind plate over the current frame (the erase).
        private static void FillFrom(Mat plate, Mat frame, Rect r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var src = new Mat(plate, r))
            using (var dst = new Mat(frame, r))
                src.CopyTo(dst);
        }

        private void PushHistory(Mat frame, long nowMs)
        {
            // Throttle to ~15fps so the always-on buffer doesn't balloon in memory.
            if (_history.Count == 0 || nowMs - _history[_history.Count - 1].Key >= StoreIntervalMs)
                _history.Add(new KeyValuePair<long, Mat>(nowMs, frame.Clone()));
            long cutoff = nowMs - (long)(MaxHistorySeconds * 1000);
            int drop = 0;
            while (drop < _history.Count && _history[drop].Key < cutoff) { _history[drop].Value.Dispose(); drop++; }
            if (drop > 0) _history.RemoveRange(0, drop);
        }

        private void ClearHistory()
        {
            foreach (var kv in _history) kv.Value.Dispose();
            _history.Clear();
        }

        // Newest plate at or before (now - secondsBack); null if the buffer isn't that
        // deep yet. "At or before" so we never fill from a frame newer than requested.
        private Mat GetHistoryFrame(long nowMs, double secondsBack)
        {
            long target = nowMs - (long)(secondsBack * 1000);
            Mat best = null; long bestKey = long.MinValue;
            foreach (var kv in _history)
                if (kv.Key <= target && kv.Key > bestKey) { bestKey = kv.Key; best = kv.Value; }
            return best;
        }

        public void Dispose()
        {
            _detector?.Dispose();
            _recognizer?.Dispose();
            _vbm?.Dispose();
            ClearHistory();
        }
    }
}
