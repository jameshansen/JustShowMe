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
    }

    /// A face detector. Two implementations: YuNetFaceDetector (DNN, any angle) and
    /// HaarFaceDetector (cascade, frontal-only fallback).
    internal interface IFaceDetector : IDisposable
    {
        string Name { get; }
        IReadOnlyList<FaceDetection> Detect(Mat bgr);
    }
}
