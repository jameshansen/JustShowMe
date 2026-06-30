using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JustShowMe.Filter;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace JustShowMe
{
    /// The always-on pump: opens the real camera, runs the loaded filter on every
    /// frame, and pushes the result into the virtual camera. The GUI must be
    /// running for this to work.
    public sealed class Pump : IDisposable
    {
        private readonly IFrameFilter _filter;
        private readonly VirtualWebcam _vcam;
        private VideoCapture _camera;
        private Timer _timer;
        private int _interval;
        private readonly object _lock = new object();   // ponytail: one lock guarding _lastRaw
        private Mat _lastRaw;

        /// Mutated by the GUI as the user changes mode / allowed faces.
        public FilterSettings Settings { get; } = new FilterSettings();

        /// Frozen, UI-thread-safe preview frames: (before, after, foreground mask,
        /// background plate). Mask/plate are null unless their feature is active.
        public event Action<ImageSource, ImageSource, ImageSource, ImageSource> FrameReady;
        public event Action<IReadOnlyList<DetectedFaceInfo>> FacesReady;

        public bool IsRunning { get; private set; }
        public bool VirtualCamConnected => _vcam.IsConnected;

        public Pump(IFrameFilter filter, VirtualWebcam vcam)
        {
            _filter = filter;
            _vcam = vcam;
        }

        public bool Start(int cameraIndex, int fps)
        {
            if (IsRunning) return true;
            _camera = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_camera.IsOpened()) { _camera.Release(); _camera = null; return false; }

            _vcam.Start(); // ok if driver missing; SendFrame is a no-op then
            IsRunning = true;
            _interval = Math.Max(1, 1000 / Math.Max(1, fps));
            // One-shot timer re-armed at the end of each Tick: never re-entrant, so
            // OpenCV native calls can't overlap (that would access-violate silently).
            _timer = new Timer(Tick, null, 0, Timeout.Infinite);
            Log.Write($"Pump started: camera {cameraIndex}, {fps} fps, vcam active={_vcam.IsActive}");
            return true;
        }

        public void Stop()
        {
            IsRunning = false;
            _timer?.Dispose(); _timer = null;
            _camera?.Release(); _camera = null;
            _vcam.Stop();
            lock (_lock) { _lastRaw?.Dispose(); _lastRaw = null; }
        }

        private void Tick(object _)
        {
            if (!IsRunning || _camera == null) return;
            try
            {
                using (var raw = new Mat())
                {
                    if (!_camera.Read(raw) || raw.Empty()) return;

                    var before = raw.ToBitmapSource();         // unfiltered, for the "Before" pane
                    before.Freeze();

                    var frame = raw.Clone();                   // working copy to blur
                    IReadOnlyList<DetectedFaceInfo> faces;
                    try { faces = _filter.Process(frame, Settings); }
                    catch (Exception ex) { Log.Write("filter.Process", ex); frame.Dispose(); return; }

                    _vcam.SendFrame(frame);

                    var after = frame.ToBitmapSource();
                    after.Freeze();

                    // Optional preview panes. Read straight after Process (same thread,
                    // before the filter reuses the Mats) and only convert when shown.
                    ImageSource mask = null, plate = null;
                    if (Settings.ForegroundMaskEnabled)
                    {
                        mask = Snapshot(_filter.ForegroundMask);
                        plate = Snapshot(_filter.VirtualBackground);
                    }

                    lock (_lock) { _lastRaw?.Dispose(); _lastRaw = raw.Clone(); } // unfiltered copy for thumbnails
                    frame.Dispose();

                    FrameReady?.Invoke(before, after, mask, plate);
                    FacesReady?.Invoke(faces);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Pump.Tick", ex);
            }
            finally
            {
                // Re-arm for the next frame only after this one fully finished.
                if (IsRunning) { try { _timer?.Change(_interval, Timeout.Infinite); } catch { } }
            }
        }

        // Frozen BitmapSource copy of a filter Mat (mask or plate), or null.
        private static ImageSource Snapshot(Mat m)
        {
            if (m == null || m.Empty()) return null;
            var bs = m.ToBitmapSource();
            bs.Freeze();
            return bs;
        }

        /// Crops a 64x64 thumbnail of a face from the last raw frame. UI-thread safe.
        public ImageSource GetThumbnail(Rect box)
        {
            lock (_lock)
            {
                if (_lastRaw == null || _lastRaw.Empty()) return null;
                // Pad ~20% so the thumbnail isn't a tight crop, then clamp to the frame.
                // (The old clamp shifted edge faces sideways — kept full width from x=0
                // when box.X was negative — which showed only a corner of the face.)
                int px = box.Width / 5, py = box.Height / 5;
                int x = box.X - px, y = box.Y - py, w = box.Width + 2 * px, h = box.Height + 2 * py;
                if (x < 0) { w += x; x = 0; }
                if (y < 0) { h += y; y = 0; }
                if (x + w > _lastRaw.Width) w = _lastRaw.Width - x;
                if (y + h > _lastRaw.Height) h = _lastRaw.Height - y;
                if (w <= 0 || h <= 0) return null;
                using (var crop = new Mat(_lastRaw, new Rect(x, y, w, h)))
                using (var small = new Mat())
                {
                    Cv2.Resize(crop, small, new Size(64, 64));
                    var bs = small.ToBitmapSource();
                    bs.Freeze();
                    return bs;
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _filter?.Dispose();
            _vcam?.Dispose();
        }
    }
}
