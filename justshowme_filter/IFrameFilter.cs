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

    /// How to obscure each acted-on person:
    ///   VirtualBackground - no per-person work; replace the whole background with the
    ///                       in-memory virtual background and keep the live foreground.
    ///   BlurFace          - blur just the face box.
    ///   BlurPerson        - blur the whole-body region (anchored on the face).
    ///   SmartFill         - replace the whole-body region with recent background,
    ///                       erasing the person (works as they enter the scene).
    public enum PersonMode
    {
        BlurFace,
        BlurPerson,
        SmartFill,
        VirtualBackground
    }

    /// How Smart Fill sources the pixels it paints over an erased person:
    ///   Rewind            - copy the region from a recent clean frame (the original
    ///                       behaviour; works as someone walks into shot).
    ///   VirtualBackground - copy from the accumulated virtual background built up (using
    ///                       the mask + people tracking) from frames where no one was
    ///                       there. Handles someone who stays put for a while.
    public enum SmartFillMode
    {
        Rewind,
        VirtualBackground
    }

    /// Settings the GUI hands to the filter every frame.
    public sealed class FilterSettings
    {
        public FilterMode Mode = FilterMode.BlurNotAllowed;
        public PersonMode PersonMode = PersonMode.BlurFace; // blur face / blur person / smart-fill person.
        public SmartFillMode SmartFillMode = SmartFillMode.Rewind; // how Smart Fill sources pixels.
        public bool ForegroundMaskEnabled = false;          // run person segmentation (Zoom-style mask).
        public int ForegroundPad = 0;                       // dilate the foreground mask by this many px.
        public double BodyScale = 3.2;                      // whole-person region width, in face widths.
        public double SmartFillSeconds = 1.0;               // how far back to pull the rewind plate.
        public int GhostSustainFrames = 90;                 // tracker persistence; GUI sets from seconds × fps.
        public int BlurStrength = 51;                       // Gaussian kernel size; forced odd by the filter.

        /// Face embeddings (from SFace) the user has allowed. A face is left clear
        /// when it matches one of these by cosine similarity — recognition, not the
        /// per-frame track id, so allowing survives the person leaving and returning.
        /// ponytail: the GUI publishes a fresh list on change; reference swap is atomic.
        public List<float[]> AllowedEmbeddings = new List<float[]>();
    }

    /// One face the filter found this frame. The GUI maps Id -> name/thumbnail and
    /// keeps the latest Embedding so it can add it to the allowed list on request.
    public struct DetectedFaceInfo
    {
        public int Id;
        public Rect Box;
        public float[] Embedding;
        /// True only when detected this frame (not coasting on tracker persistence).
        /// The GUI uses this to avoid grabbing a thumbnail at a stale box position.
        public bool Seen;
    }

    /// "Is this the same person?" by SFace embedding. Shared by the filter (blur
    /// decision) and the GUI (so one person is one list row, not one per track id).
    public static class FaceMatch
    {
        /// Cosine cutoff for "same identity". SFace's tuned value is 0.363; default a
        /// touch stricter because for a privacy tool a false match un-blurs the wrong
        /// person — the costly direction. A miss merely re-blurs a known face.
        /// Mutable so the GUI's strictness slider can tune it (persisted to the ini).
        /// ponytail: plain static — filter + GUI share the one loaded assembly.
        public static double Threshold = 0.40;

        public static bool IsSame(float[] a, float[] b) => Cosine(a, b) >= Threshold;

        public static double Cosine(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return -1;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na <= 0 || nb <= 0) return -1;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }
    }

    /// The contract a swappable filter DLL must implement. The GUI loads the
    /// configured DLL by path, finds the single IFrameFilter, and calls Process
    /// on every frame. Custom filters reference this assembly for the interface.
    public interface IFrameFilter : IDisposable
    {
        /// Processes the BGR frame in place. Returns the faces seen this frame.
        IReadOnlyList<DetectedFaceInfo> Process(Mat frame, FilterSettings settings);

        /// Latest foreground (subject) mask and the in-memory virtual background, for the
        /// GUI previews. Null unless their feature is active this frame. Read on the
        /// pump thread right after Process; owned by the filter, don't dispose.
        Mat ForegroundMask { get; }
        Mat VirtualBackground { get; }
    }
}
