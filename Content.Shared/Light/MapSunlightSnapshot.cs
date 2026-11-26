// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared.Light;

// Sunlight Changes: Send clipped receiver geometry so the client never has to infer shadows from streamed fixtures.
[Serializable, NetSerializable]
public sealed class MapSunlightSnapshot(MapSunlightRegion[] regions) : EntityEventArgs
{
    public readonly MapSunlightRegion[] Regions = regions;
}

// Sunlight Changes: Keep receiver coverage with the geometry so filters cannot blend an unseen region into a shadow.
[Serializable, NetSerializable]
public readonly record struct MapSunlightRegion(NetEntity Map, Box2 Bounds, Vector2 Direction, SunlightTrapezoid[] Lit);

[Serializable, NetSerializable]
public readonly record struct SunlightTrapezoid(float MinOffset, float MaxOffset,
    float StartAtMin, float StartAtMax, float EndAtMin, float EndAtMax);
