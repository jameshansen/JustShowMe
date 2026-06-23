using System;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace JustShowMe
{
    /// Sender side of the JustShowMe virtual camera. Pushes filtered frames into
    /// the shared frame buffer that the registered DirectShow driver serves to
    /// consuming apps. Thin P/Invoke wrapper over the driver's C "sc" API.
    public sealed class VirtualWebcam : IDisposable
    {
        private const string Dll = "justshowme_cam.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr scCreateCamera(int width, int height, float framerate);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void scDeleteCamera(IntPtr camera);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void scSendFrame(IntPtr camera, IntPtr image_bits);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool scIsConnected(IntPtr camera);

        private IntPtr _camera = IntPtr.Zero;
        private readonly int _width, _height;
        private readonly float _fps;
        private byte[] _buffer;

        public bool IsActive => _camera != IntPtr.Zero;
        public bool IsConnected => IsActive && scIsConnected(_camera);

        public VirtualWebcam(int width, int height, float fps)
        {
            _width = width; _height = height; _fps = fps;
            _buffer = new byte[width * height * 3];
        }

        /// Returns false if the driver isn't installed/registered (DllNotFound).
        public bool Start()
        {
            if (IsActive) return true;
            try { _camera = scCreateCamera(_width, _height, _fps); }
            catch (DllNotFoundException) { return false; }
            return IsActive;
        }

        public void Stop()
        {
            if (!IsActive) return;
            scDeleteCamera(_camera);
            _camera = IntPtr.Zero;
        }

        public void SendFrame(Mat frame)
        {
            if (!IsActive || frame == null || frame.Empty()) return;
            using (var resized = new Mat())
            {
                Cv2.Resize(frame, resized, new Size(_width, _height));
                // DirectShow RGB24 is physically B,G,R in memory — i.e. OpenCV's native
                // BGR order. Send as-is; converting to RGB is what swaps the colours.
                Marshal.Copy(resized.Data, _buffer, 0, _buffer.Length);
                var h = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                try { scSendFrame(_camera, h.AddrOfPinnedObject()); }
                finally { h.Free(); }
            }
        }

        public void Dispose() => Stop();
    }
}
