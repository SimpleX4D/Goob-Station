// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.Light;

namespace Content.Server.Light.EntitySystems;

/// <summary>Sunlight Changes: A line in sunlight coordinates, with length measured along the transverse axis.</summary>
internal readonly record struct SunlightSegment(float Min, float Max, double Slope, double Intercept)
{
    public static SunlightSegment FromPoints(Vector2 first, Vector2 second)
    {
        var slope = ((double) second.X - first.X) / ((double) second.Y - first.Y);
        return new SunlightSegment(Math.Min(first.Y, second.Y), Math.Max(first.Y, second.Y), slope, first.X - slope * first.Y);
    }

    public double At(double offset) => Slope * offset + Intercept;

    public float Intersection(SunlightSegment other) => (float) ((other.Intercept - Intercept) / (Slope - other.Slope));
}

internal static class SunlightEnvelope
{
    // Sunlight Changes: Reduce overlapping contours to the nearest surface for each ray instead of rasterizing each wall separately.
    public static List<SunlightSegment> Build(List<SunlightSegment> edges)
    {
        return Build(edges, 0, edges.Count);
    }

    private static List<SunlightSegment> Build(List<SunlightSegment> edges, int start, int count)
    {
        if (count == 0)
            return [];
        if (count == 1)
            return [edges[start]];

        var half = count / 2;
        return Merge(Build(edges, start, half), Build(edges, start + half, count - half));
    }

    // Sunlight Changes: Split overlapping edges when their depths cross so the correct occluder wins along the whole span.
    private static List<SunlightSegment> Merge(List<SunlightSegment> left, List<SunlightSegment> right)
    {
        var result = new List<SunlightSegment>(Math.Max(left.Count, right.Count));
        var i = 0;
        var j = 0;
        var offset = Math.Min(left[0].Min, right[0].Min);
        while (i < left.Count || j < right.Count)
        {
            var a = i < left.Count ? left[i] : default;
            var b = j < right.Count ? right[j] : default;
            var activeA = i < left.Count && a.Min <= offset;
            var activeB = j < right.Count && b.Min <= offset;
            var nextA = i == left.Count ? float.PositiveInfinity : activeA ? a.Max : a.Min;
            var nextB = j == right.Count ? float.PositiveInfinity : activeB ? b.Max : b.Min;
            var end = Math.Min(nextA, nextB);

            if (activeA && activeB)
            {
                var crossing = a.Intersection(b);
                if (crossing > offset && crossing < end)
                {
                    AppendNearest(result, a, b, offset, crossing);
                    AppendNearest(result, a, b, crossing, end);
                }
                else
                {
                    AppendNearest(result, a, b, offset, end);
                }
            }
            else if (activeA || activeB)
            {
                Append(result, activeA ? a : b, offset, end);
            }

            offset = end;
            if (activeA && a.Max <= offset)
                i++;
            if (activeB && b.Max <= offset)
                j++;
        }

        return result;
    }

    private static void AppendNearest(List<SunlightSegment> result, SunlightSegment a, SunlightSegment b, float start, float end)
    {
        var middle = ((double) start + end) * 0.5;
        Append(result, a.At(middle) <= b.At(middle) ? a : b, start, end);
    }

    private static void Append(List<SunlightSegment> result, SunlightSegment line, float start, float end)
    {
        if (start >= end)
            return;

        if (result.Count > 0 && result[^1] is var last && last.Max == start
            && last.Slope == line.Slope && last.Intercept == line.Intercept)
            result[^1] = last with { Max = end };
        else
            result.Add(line with { Min = start, Max = end });
    }

    // Sunlight Changes: Convert a rotated receiver into linear bands so clipping remains exact at oblique angles.
    public static int GetViewSlices(Box2 bounds, Matrix3x2 worldToSun, Span<ViewSlice> slices)
    {
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            Vector2.Transform(bounds.BottomLeft, worldToSun), Vector2.Transform(bounds.BottomRight, worldToSun),
            Vector2.Transform(bounds.TopRight, worldToSun), Vector2.Transform(bounds.TopLeft, worldToSun),
        };
        Span<float> offsets = stackalloc float[4];
        Span<SunlightSegment> edges = stackalloc SunlightSegment[4];
        var edgeCount = 0;
        for (var i = 0; i < corners.Length; i++)
        {
            var first = corners[i];
            var second = corners[(i + 1) % corners.Length];
            offsets[i] = first.Y;
            if (first.Y != second.Y)
                edges[edgeCount++] = SunlightSegment.FromPoints(first, second);
        }

        offsets.Sort();
        var count = 0;
        for (var i = 1; i < offsets.Length; i++)
        {
            var start = offsets[i - 1];
            var end = offsets[i];
            if (start == end)
                continue;

            var middle = ((double) start + end) * 0.5;
            var near = default(SunlightSegment);
            var far = default(SunlightSegment);
            var nearDepth = double.PositiveInfinity;
            var farDepth = double.NegativeInfinity;
            foreach (var edge in edges[..edgeCount])
            {
                if (middle < edge.Min || middle > edge.Max)
                    continue;

                var depth = edge.At(middle);
                if (depth < nearDepth)
                    (near, nearDepth) = (edge, depth);
                if (depth > farDepth)
                    (far, farDepth) = (edge, depth);
            }

            if (nearDepth < farDepth)
                slices[count++] = new ViewSlice(start, end, near, far);
        }

        return count;
    }

    // Sunlight Changes: Emit lit gaps as well as clipped light strips so distant walls cannot shade the rest of the receiver by accident.
    public static void ClipEnvelope(List<SunlightSegment> envelope, ReadOnlySpan<ViewSlice> view, List<SunlightTrapezoid> output)
    {
        if (view.IsEmpty)
            return;

        var edgeIndex = 0;
        var limit = envelope.Count;
        while (edgeIndex < limit)
        {
            var middle = edgeIndex + (limit - edgeIndex) / 2;
            if (envelope[middle].Max <= view[0].Min)
                edgeIndex = middle + 1;
            else
                limit = middle;
        }

        foreach (var slice in view)
        {
            var cursor = slice.Min;
            while (edgeIndex < envelope.Count && cursor < slice.Max)
            {
                var edge = envelope[edgeIndex];
                if (edge.Max <= cursor)
                {
                    edgeIndex++;
                    continue;
                }

                if (edge.Min >= slice.Max)
                    break;

                if (cursor < edge.Min)
                    ClipStrip(slice, slice.Far, cursor, edge.Min, output);

                ClipStrip(slice, edge, Math.Max(cursor, edge.Min), Math.Min(slice.Max, edge.Max), output);
                cursor = edge.Max;
            }

            if (cursor < slice.Max)
                ClipStrip(slice, slice.Far, cursor, slice.Max, output);
        }
    }

    // Sunlight Changes: Split at depth crossings so trapezoids cannot turn inside out on a grazing edge.
    public static void ClipStrip(ViewSlice slice, SunlightSegment shadow, float start, float end, List<SunlightTrapezoid> output)
    {
        start = Math.Max(start, slice.Min);
        end = Math.Min(end, slice.Max);
        if (start >= end)
            return;

        Span<float> cuts = stackalloc float[4];
        cuts[0] = start;
        cuts[1] = end;
        var count = 2;
        var entrance = shadow.Intersection(slice.Near);
        var exit = shadow.Intersection(slice.Far);
        if (entrance > start && entrance < end)
            cuts[count++] = entrance;
        if (exit > start && exit < end)
            cuts[count++] = exit;

        cuts[..count].Sort();
        for (var i = 1; i < count; i++)
        {
            var low = cuts[i - 1];
            var high = cuts[i];
            var middle = ((double) low + high) * 0.5;
            if (low == high || shadow.At(middle) <= slice.Near.At(middle))
                continue;

            var nearLow = slice.Near.At(low);
            var nearHigh = slice.Near.At(high);
            var farLow = slice.Far.At(low);
            var farHigh = slice.Far.At(high);
            output.Add(new SunlightTrapezoid(low, high, (float) nearLow, (float) nearHigh,
                (float) Math.Clamp(shadow.At(low), nearLow, Math.Max(nearLow, farLow)),
                (float) Math.Clamp(shadow.At(high), nearHigh, Math.Max(nearHigh, farHigh))));
        }
    }

    internal readonly record struct ViewSlice(float Min, float Max, SunlightSegment Near, SunlightSegment Far);
}
