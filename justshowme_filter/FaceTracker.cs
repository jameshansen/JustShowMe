using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// Lightweight IoU tracker. Gives faces stable ids and keeps a face "alive" for
    /// a few frames after the detector stops seeing it — so a head turning through an
    /// angle the detector momentarily misses stays blurred instead of flashing clear.
    /// ponytail: greedy IoU match, no Kalman/Hungarian — fine at these counts.
    internal sealed class FaceTracker
    {
        private sealed class Track
        {
            public int Id;
            public Rect Box;
            public int Age; // frames since last detection (0 = seen this frame)
        }

        private readonly List<Track> _tracks = new List<Track>();
        private readonly double _iouThreshold;
        private readonly int _maxAge;   // keep blurring this many frames after the last hit
        private int _nextId = 1;

        public FaceTracker(double iouThreshold = 0.3, int maxAge = 15)
        {
            _iouThreshold = iouThreshold;
            _maxAge = maxAge;
        }

        /// Feeds this frame's detections in; returns the id+box of every active
        /// track to blur (including recently-seen ones within the persistence window).
        public IReadOnlyList<KeyValuePair<int, Rect>> Update(IReadOnlyList<Rect> detections)
        {
            foreach (var t in _tracks) t.Age++;

            var trackUsed = new bool[_tracks.Count];
            var detMatched = new bool[detections.Count];

            // Greedy IoU matching: each detection to its best still-free track.
            for (int d = 0; d < detections.Count; d++)
            {
                int best = -1;
                double bestIou = _iouThreshold;
                for (int i = 0; i < _tracks.Count; i++)
                {
                    if (trackUsed[i]) continue;
                    double iou = IoU(_tracks[i].Box, detections[d]);
                    if (iou > bestIou) { bestIou = iou; best = i; }
                }
                if (best >= 0)
                {
                    _tracks[best].Box = detections[d];
                    _tracks[best].Age = 0;
                    trackUsed[best] = true;
                    detMatched[d] = true;
                }
            }

            // Unmatched detections become new tracks (blurred immediately — for a
            // privacy tool, erring toward over-blur is the safe choice).
            for (int d = 0; d < detections.Count; d++)
                if (!detMatched[d])
                    _tracks.Add(new Track { Id = _nextId++, Box = detections[d], Age = 0 });

            _tracks.RemoveAll(t => t.Age > _maxAge);

            var active = new List<KeyValuePair<int, Rect>>(_tracks.Count);
            foreach (var t in _tracks)
                active.Add(new KeyValuePair<int, Rect>(t.Id, t.Box));
            return active;
        }

        private static double IoU(Rect a, Rect b)
        {
            int x1 = Math.Max(a.Left, b.Left), y1 = Math.Max(a.Top, b.Top);
            int x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
            int iw = x2 - x1, ih = y2 - y1;
            if (iw <= 0 || ih <= 0) return 0;
            double inter = (double)iw * ih;
            double union = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
            return union <= 0 ? 0 : inter / union;
        }
    }
}
