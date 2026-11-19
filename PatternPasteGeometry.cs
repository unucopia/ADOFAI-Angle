using System;

namespace Angle
{
    internal struct PatternPasteTransform
    {
        private readonly float sourceReference;
        private readonly float targetReference;
        private readonly float sign;

        internal bool IsMirrored => sign < 0f;
        internal bool IsIdentity => !IsMirrored
            && Math.Abs(PatternPasteGeometry.SignedDifference(
                targetReference, sourceReference)) < 0.001f;

        internal PatternPasteTransform(
            float sourceReference, float targetReference, bool mirror)
        {
            this.sourceReference = sourceReference;
            this.targetReference = targetReference;
            sign = mirror ? -1f : 1f;
        }

        internal float Apply(float angle)
        {
            // 999 is a midspin marker, not an angle to rotate or mirror.
            return PatternPasteGeometry.IsDirection(angle)
                ? PatternPasteGeometry.Normalize(
                    targetReference + sign * (angle - sourceReference))
                : angle;
        }
    }

    internal static class PatternPasteGeometry
    {
        internal static bool IsDirection(float angle)
        {
            return !float.IsNaN(angle)
                && !float.IsInfinity(angle)
                && angle != 999f;
        }

        internal static float Normalize(float angle)
        {
            float result = angle % 360f;
            return result < 0f ? result + 360f : result;
        }

        internal static float SignedDifference(float target, float source)
        {
            return Normalize(target - source + 180f) - 180f;
        }

        internal static PatternPasteTransform Create(
            float firstDirection,
            float? sourceReference,
            bool? sourceIsCCW,
            float targetReference,
            bool targetIsCCW,
            float? forcedDirection)
        {
            if (forcedDirection.HasValue)
            {
                float source = Normalize(firstDirection);
                float target = Normalize(forcedDirection.Value);
                float difference = Math.Abs(SignedDifference(target, source));
                if (difference < 0.001f)
                {
                    return new PatternPasteTransform(0f, 0f, false);
                }
                // Reverse the direction of travel without turning the other
                // axis upside down. Other changes turn the whole shape rigidly.
                // Thus left -> right flips horizontally; right -> up rotates.
                return new PatternPasteTransform(
                    source, target, Math.Abs(difference - 180f) < 0.001f);
            }
            bool mirror = sourceIsCCW.HasValue
                && sourceIsCCW.Value != targetIsCCW;
            // Old files have no entry reference. Align their first real track
            // instead, while preserving every angle inside the saved pattern.
            float sourceAnchor = !sourceReference.HasValue
                || !IsDirection(sourceReference.Value)
                    ? firstDirection
                    : sourceReference.Value;
            return new PatternPasteTransform(
                sourceAnchor,
                targetReference,
                mirror);
        }
    }
}
