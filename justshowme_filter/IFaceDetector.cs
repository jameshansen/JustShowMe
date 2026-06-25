using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace JustShowMe.Filter
{
    /// One detected face in image coordinates.
    internal struct FaceDetection
    {
        public Rect Box;
        public float Score;
        /// YuNet's 5 landmarks (right eye, left eye, nose, right & left mouth
        /// corners) in image coords — used to align the crop for SFace. May be null.
        public Point2f[] Landmarks;
    }

    /// A face detector. Two implementations: YuNetFaceDetector (DNN, any angle) and
    /// HaarFaceDetector (cascade, frontal-only fallback).
    internal interface IFaceDetector : IDisposable
    {
        string Name { get; }
        IReadOnlyList<FaceDetection> Detect(Mat bgr);
    }
}
