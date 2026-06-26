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
        /// What a track exposes each frame: stable id, current box, the last embedding
        /// seen for it (kept through dropouts so the blur decision holds), and whether
        /// it was actually detected THIS frame (Seen) vs coasting on persistence — the
        /// box of a coasting track is stale, so it must not be used for thumbnails.
        public struct TrackedFace
        {
            public int Id;
            public Rect Box;
            public float[] Embedding;
            public bool Seen;
        }

        private sealed class Track
        {
            public int Id;
            public Rect Box;
            public float[] Embedding;
            public int Age; // frames since last detection (0 = seen this frame)
        }

        // Cosine cutoff for re-attaching a detection to an existing track by appearance
        // when boxes don't overlap. Lenient — merging the same person aggressively is
        // safe; the per-frame blur/allow decision still uses the live embedding.
        private const double MergeSimilarity = 0.35;

        private readonly List<Track> _tracks = new List<Track>();
        private readonly double _iouThreshold;
        private int _nextId = 1;

        /// Keep a track alive this many frames after its last detection (GUI-tunable).
        public int MaxAge { get; set; }

        // maxAge in FRAMES (tracker can't see fps): 90 ≈ 3s at 30fps, so a face
        // keeps its id — and its allowed/excluded status — through a multi-second
        // detector dropout (head turn, motion blur) instead of coming back as a
        // new "New Face". iou 0.2 tolerates the box growing/shrinking with pose.
        // ponytail: tuning, not new logic. Cross-discontinuity identity (a person
        // re-entering, or a looping video's seam) needs appearance recognition.
        public FaceTracker(double iouThreshold = 0.2, int maxAge = 90)
        {
            _iouThreshold = iouThreshold;
            MaxAge = maxAge;
        }

        /// Feeds this frame's detections (with their SFace embeddings, parallel array)
        /// in; returns every active track — including recently-seen ones within the
        /// persistence window, which keep their last box and embedding.
        public IReadOnlyList<TrackedFace> Update(IReadOnlyList<Rect> detections, IReadOnlyList<float[]> embeddings)
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
                    _tracks[best].Embedding = embeddings[d] ?? _tracks[best].Embedding;
                    _tracks[best].Age = 0;
                    trackUsed[best] = true;
                    detMatched[d] = true;
                }
            }

            // Appearance fallback: a detection that overlapped no track (fast move, a
            // jump near the frame edge) is matched to the most similar still-free track
            // by SFace embedding — so the same person keeps ONE track instead of leaving
            // a trail of ghost tracks (each of which would keep blurring/filling its old
            // spot). Lenient fixed cutoff, independent of the GUI allow-strictness.
            for (int d = 0; d < detections.Count; d++)
            {
                if (detMatched[d] || embeddings[d] == null) continue;
                int best = -1;
                double bestSim = MergeSimilarity;
                for (int i = 0; i < _tracks.Count; i++)
                {
                    if (trackUsed[i] || _tracks[i].Embedding == null) continue;
                    double sim = FaceMatch.Cosine(embeddings[d], _tracks[i].Embedding);
                    if (sim > bestSim) { bestSim = sim; best = i; }
                }
                if (best >= 0)
                {
                    _tracks[best].Box = detections[d];
                    _tracks[best].Embedding = embeddings[d];
                    _tracks[best].Age = 0;
                    trackUsed[best] = true;
                    detMatched[d] = true;
                }
            }

            // Whatever's still unmatched becomes a new track (blurred immediately — for
            // a privacy tool, erring toward over-blur is the safe choice).
            for (int d = 0; d < detections.Count; d++)
                if (!detMatched[d])
                    _tracks.Add(new Track { Id = _nextId++, Box = detections[d], Embedding = embeddings[d], Age = 0 });

            _tracks.RemoveAll(t => t.Age > MaxAge);

            var active = new List<TrackedFace>(_tracks.Count);
            foreach (var t in _tracks)
                active.Add(new TrackedFace { Id = t.Id, Box = t.Box, Embedding = t.Embedding, Seen = t.Age == 0 });
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
