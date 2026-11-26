// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared.Light;

// Sunlight Changes: Send clipped receiver geometry so the client never has to infer shadows from streamed fixtures.
[Serializable, NetSerializable]
public sealed class MapSunlightSnapshot(MapSunlightRegion[] regions, MapSunlightGrid[] grids) : EntityEventArgs
{
    public readonly MapSunlightRegion[] Regions = regions;
    public readonly MapSunlightGrid[] Grids = grids;

    public bool SameRegions(MapSunlightSnapshot other)
    {
        if (Regions.Length != other.Regions.Length)
            return false;

        for (var i = 0; i < Regions.Length; i++)
        {
            var left = Regions[i];
            var right = other.Regions[i];
            if (left.Map != right.Map || left.Direction != right.Direction || left.Bounds != right.Bounds
                || !left.Lit.AsSpan().SequenceEqual(right.Lit))
                return false;
        }

        return true;
    }

    public bool SameGrids(MapSunlightSnapshot other)
    {
        if (Grids.Length != other.Grids.Length)
            return false;

        for (var i = 0; i < Grids.Length; i++)
        {
            var left = Grids[i];
            var right = other.Grids[i];
            if (left.Position != right.Position || left.Rotation != right.Rotation || !left.SameContours(right))
                return false;
        }

        return true;
    }
}

// Sunlight Changes: Keep receiver coverage with the geometry so filters cannot blend an unseen region into a shadow.
[Serializable, NetSerializable]
public readonly record struct MapSunlightRegion(NetEntity Map, Box2 Bounds, Vector2 Direction, SunlightTrapezoid[] Lit);

// Sunlight Changes: Retain grid-local contours so the client can follow interpolated motion between snapshots.
[Serializable, NetSerializable]
public readonly record struct MapSunlightGrid(NetEntity Map, NetEntity Grid, Vector2 Position, Angle Rotation, Vector2[][] Contours)
{
    public bool SameContours(MapSunlightGrid other)
    {
        if (Map != other.Map || Grid != other.Grid || Contours.Length != other.Contours.Length)
            return false;

        for (var i = 0; i < Contours.Length; i++)
        {
            if (!Contours[i].AsSpan().SequenceEqual(other.Contours[i]))
                return false;
        }

        return true;
    }
}

[Serializable, NetSerializable]
public readonly record struct SunlightTrapezoid(float MinOffset, float MaxOffset,
    float StartAtMin, float StartAtMax, float EndAtMin, float EndAtMax);
