// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Content.Shared.Light;

public static class SunlightProjectionGeometry
{
    // Sunlight Changes: The fixtures use convex polygons of at most eight points. Extrusion adds two vertices,
    // and clipping against a rectangle can add four more.
    public const int MaxVertices = 16;

    /// <summary>Sunlight Changes: Extrudes the light-facing contour of a CCW convex polygon in world space.</summary>
    public static int Project(ReadOnlySpan<Vector2> contour, Matrix3x2 localToWorld, Vector2 direction,
        float farDepth, Span<Vector2> result)
    {
        var across = new Vector2(-direction.Y, direction.X);
        var min = 0;
        var max = 0;
        var minOffset = float.PositiveInfinity;
        var maxOffset = float.NegativeInfinity;
        var minOffsetDepth = float.PositiveInfinity;
        var maxOffsetDepth = float.PositiveInfinity;
        var closestDepth = float.PositiveInfinity;
        var backDepth = float.NegativeInfinity;

        for (var i = 0; i < contour.Length; i++)
        {
            var point = Vector2.Transform(contour[i], localToWorld);
            var offset = Vector2.Dot(point, across);
            var depth = Vector2.Dot(point, direction);
            if (offset < minOffset || offset == minOffset && depth < minOffsetDepth)
                (min, minOffset, minOffsetDepth) = (i, offset, depth);
            if (offset > maxOffset || offset == maxOffset && depth < maxOffsetDepth)
                (max, maxOffset, maxOffsetDepth) = (i, offset, depth);
            closestDepth = Math.Min(closestDepth, depth);
            backDepth = Math.Max(backDepth, depth);
        }

        if (maxOffset <= minOffset || closestDepth >= farDepth)
            return 0;

        // Sunlight Changes: A contour can extend beyond the receiver. Keeping the extrusion convex makes
        // the result safe to clip and draw as one fan, even at rotation extremes.
        farDepth = Math.Max(farDepth, backDepth);
        var count = 0;
        for (var i = max;; i = (i + 1) % contour.Length)
        {
            result[count++] = Vector2.Transform(contour[i], localToWorld);
            if (i == min)
                break;
        }

        result[count] = result[count - 1] + direction * (farDepth - minOffsetDepth);
        result[count + 1] = result[0] + direction * (farDepth - maxOffsetDepth);
        return count + 2;
    }

    public static float FarDepth(Box2 bounds, Vector2 direction)
    {
        var x = direction.X >= 0f ? bounds.Right : bounds.Left;
        var y = direction.Y >= 0f ? bounds.Top : bounds.Bottom;
        return Vector2.Dot(new Vector2(x, y), direction);
    }
}
