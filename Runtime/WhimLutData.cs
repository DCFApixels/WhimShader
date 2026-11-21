using System;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    public enum WhimLutKind { Gradient, Curve }

    /// <summary>Editable source data. LUT coordinates always cover normalized time [0, 1].</summary>
    [Serializable]
    public sealed class WhimLutData
    {
        public const int DefaultResolution = 256;
        public const int MinResolution = 2;
        public const int MaxResolution = 4096;

        public WhimLutKind kind;
        public int resolution = DefaultResolution;
        public Gradient gradient = new Gradient();
        public AnimationCurve curve = AnimationCurve.Linear(0, 0, 1, 1);

        public WhimLutData Clone()
        {
            var result = new WhimLutData { kind = kind, resolution = resolution };
            if (gradient != null)
            {
                result.gradient.SetKeys(gradient.colorKeys, gradient.alphaKeys);
                result.gradient.mode = gradient.mode;
            }
            if (curve != null)
            {
                result.curve = new AnimationCurve(curve.keys)
                {
                    preWrapMode = curve.preWrapMode,
                    postWrapMode = curve.postWrapMode
                };
            }
            return result;
        }

        public void Validate()
        {
            if (resolution < MinResolution || resolution > MaxResolution)
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution,
                    "LUT resolution must be between 2 and 4096.");
            if (kind != WhimLutKind.Gradient && kind != WhimLutKind.Curve)
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (kind == WhimLutKind.Gradient && gradient == null)
                throw new ArgumentException("Gradient data is missing.", nameof(gradient));
            if (kind == WhimLutKind.Curve && curve == null)
                throw new ArgumentException("Curve data is missing.", nameof(curve));
        }
    }
}
