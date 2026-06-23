using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    public enum FilterMode
    {
        BlurAll,
        BlurNotAllowed
    }

    /// Settings the GUI hands to the filter every frame.
    public sealed class FilterSettings
    {
        public FilterMode Mode = FilterMode.BlurNotAllowed;
        public int BlurStrength = 51;                       // Gaussian kernel size; forced odd by the filter.
        public HashSet<int> AllowedFaceIds = new HashSet<int>();
    }

    /// One face the filter found this frame. The GUI maps Id -> name/thumbnail.
    public struct DetectedFaceInfo
    {
        public int Id;
        public Rect Box;
    }

    /// The contract a swappable filter DLL must implement. The GUI loads the
    /// configured DLL by path, finds the single IFrameFilter, and calls Process
    /// on every frame. Custom filters reference this assembly for the interface.
    public interface IFrameFilter : IDisposable
    {
        /// Processes the BGR frame in place. Returns the faces seen this frame.
        IReadOnlyList<DetectedFaceInfo> Process(Mat frame, FilterSettings settings);
    }
}
