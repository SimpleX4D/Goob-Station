// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Client.Graphics;
using Content.Client.Light.EntitySystems;
using Content.Shared.CCVar;
using Content.Shared.Light;
using Content.Shared.Light.Components;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.Light;

// Sunlight Changes: Rasterize the same opaque light strips as Full and keep the mask fixed in world space between updates.
internal sealed class MapSunlightOverlay : Overlay
{
    private const int Padding = 32;
    private const int FilterMargin = 2;
    private static readonly ProtoId<ShaderPrototype> Unshaded = "unshaded";
    private static readonly ProtoId<ShaderPrototype> Composite = "MapSunlight";
    private static readonly RenderTargetFormatParameters MaskFormat = new(RenderTargetColorFormat.Rgba8);
    private static readonly TextureSampleParameters MaskSampling = new() { Filter = true };

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly PlanetLightSystem _lighting;
    private readonly SharedTransformSystem _transform;
    private readonly Vector2[] _shadow = new Vector2[SunlightProjectionGeometry.MaxVertices];
    private readonly List<Vector2> _liveVertices = new();
    private readonly OverlayResourceCache<ViewportMask> _masks = new();
    private ShaderInstance? _composite;

    public override OverlaySpace Space => OverlaySpace.BeforeLighting;

    public MapSunlightOverlay(PlanetLightSystem lighting)
    {
        IoCManager.InjectDependencies(this);
        _lighting = lighting;
        _transform = _entities.System<SharedTransformSystem>();
        ZIndex = AfterLightTargetOverlay.ContentZIndex + 2;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var viewport = args.Viewport;
        if (viewport.Eye is not { } eye || _lighting.Sunlight is not { } snapshot
            || !_entities.TryGetComponent(args.MapUid, out MapSunlightComponent? sun) || !sun.Enabled)
            return;

        var cache = _masks.GetForViewport(viewport, static _ => new ViewportMask());
        var map = _entities.GetNetEntity(args.MapUid);
        var sameMap = cache.Map == map;
        var previousDirection = cache.Direction;
        var geometryChanged = !sameMap || cache.Snapshot == null;
        var partitionChanged = false;
        if (geometryChanged || cache.Snapshot != snapshot)
        {
            geometryChanged |= cache.Snapshot == null || !snapshot.SameRegions(cache.Snapshot);
            partitionChanged = cache.SetLiveGrids(snapshot, map);
            cache.Snapshot = snapshot;
            if (geometryChanged)
                cache.SetGeometry(snapshot, map);
        }
        if (cache.Bounds.Count == 0)
            return;

        var light = viewport.LightRenderTarget;
        var scale = viewport.RenderScale * (light.Size / (Vector2) viewport.Size);
        var worldToLight = light.GetWorldToLocalMatrix(eye, scale);
        var size = light.Size + new Vector2i(2 * Padding, 2 * Padding);
        var previous = cache.Mask;
        var previousOffset = cache.WorldToMask.Translation - worldToLight.Translation;
        var fits = previous?.Size == size && FitsMask(cache.WorldToMask, worldToLight, previousOffset);
        var temporal = _cfg.GetCVar(CCVars.StationSunlightTemporal);
        var frame = _timing.CurFrame;

        // Sunlight Changes: Consume only the previous raw frame, and discard it on map, angle, viewport or camera cuts.
        var blendPrevious = geometryChanged && !partitionChanged && sameMap && previousDirection == cache.Direction && fits
            && temporal && cache.Temporal && unchecked(cache.Frame + 1) == frame;
        if (blendPrevious)
            (cache.Mask, cache.Spare) = (cache.Spare, cache.Mask);

        if (geometryChanged || !fits)
            RenderMask(cache, args.WorldHandle, size, worldToLight);

        if (!temporal && cache.Spare is { } spare)
        {
            spare.Dispose();
            cache.Spare = null;
        }

        var mask = cache.Mask!;
        var live = RenderLiveMask(cache, args.WorldHandle, args.MapUid, size);
        var shader = _composite ??= _prototypes.Index(Composite).InstanceUnique();
        shader.SetParameter("CurrentOffset", ToTextureOffset(cache.WorldToMask.Translation - worldToLight.Translation));
        shader.SetParameter("PreviousOffset", ToTextureOffset(previousOffset));
        shader.SetParameter("PreviousMask", blendPrevious ? previous!.Texture : mask.Texture);
        shader.SetParameter("BlendPrevious", blendPrevious);
        shader.SetParameter("ApplyLiveMask", live != null);
        shader.SetParameter("LiveMask", live?.Texture ?? Texture.White);

        var bounds = args.WorldBounds;
        var handle = args.WorldHandle;
        var color = sun.Color;
        handle.RenderInRenderTarget(light, () =>
        {
            handle.SetTransform(worldToLight);
            handle.UseShader(shader);
            handle.DrawTextureRect(mask.Texture, bounds, color);
        }, null);

        cache.Frame = frame;
        cache.Temporal = temporal;
    }

    private void RenderMask(ViewportMask cache, DrawingHandleWorld handle, Vector2i size, Matrix3x2 worldToLight)
    {
        if (cache.Mask?.Size != size)
        {
            cache.Mask?.Dispose();
            cache.Mask = _clyde.CreateRenderTarget(size, MaskFormat, MaskSampling, "map-sunlight-mask");
        }

        // Sunlight Changes: Anchor the padded mask at integer pixels. Camera translations then change sampling coordinates,
        // while shared strip edges stay on the same raster grid even when the mask has to be recentered.
        var offset = worldToLight.Translation;
        worldToLight.Translation = new Vector2(MathF.Round(offset.X) + Padding, MathF.Round(offset.Y) + Padding);
        cache.WorldToMask = worldToLight;
        var direction = cache.Direction;
        var sunToWorld = new Matrix3x2(direction.X, direction.Y, -direction.Y, direction.X, 0f, 0f);
        handle.RenderInRenderTarget(cache.Mask, () =>
        {
            handle.UseShader(_prototypes.Index(Unshaded).Instance());
            handle.SetTransform(worldToLight);
            foreach (var bounds in cache.Bounds)
                handle.DrawRect(bounds, Color.Black);

            if (cache.Vertices.Count == 0)
                return;

            handle.SetTransform(sunToWorld * worldToLight);
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, cache.Vertices, Color.White);
        }, Color.Transparent);
    }

    private IRenderTexture? RenderLiveMask(ViewportMask cache, DrawingHandleWorld handle, EntityUid map, Vector2i size)
    {
        if (cache.Grids.Count == 0)
        {
            cache.LiveMask?.Dispose();
            cache.LiveMask = null;
            return null;
        }

        var dirty = cache.LiveDirty || cache.LiveTransform != cache.WorldToMask || cache.LiveDirection != cache.Direction;
        if (cache.LivePoses.Length != cache.Grids.Count)
            cache.LivePoses = new Matrix3x2?[cache.Grids.Count];

        // Sunlight Changes: Read the current interpolated grid transform, even when no lighting snapshot arrived.
        for (var i = 0; i < cache.Grids.Count; i++)
        {
            var pose = GetGridPose(_entities, _transform, cache.Grids[i], map);
            dirty |= cache.LivePoses[i] != pose;
            cache.LivePoses[i] = pose;
        }

        if (cache.LiveMask?.Size != size)
        {
            cache.LiveMask?.Dispose();
            cache.LiveMask = _clyde.CreateRenderTarget(size, new RenderTargetFormatParameters(RenderTargetColorFormat.R8),
                MaskSampling, "map-sunlight-live-mask");
            dirty = true;
        }

        if (!dirty)
            return cache.LiveMask;

        cache.LiveDirty = false;
        cache.LiveTransform = cache.WorldToMask;
        cache.LiveDirection = cache.Direction;
        Matrix3x2.Invert(cache.WorldToMask, out var maskToWorld);
        // Sunlight Changes: Extend through the padded target so reusing the mask during camera pans cannot expose the end of a shadow.
        var bounds = maskToWorld.TransformBox(new Box2(0, 0, size.X, size.Y));
        var farDepth = SunlightProjectionGeometry.FarDepth(bounds, cache.Direction);
        _liveVertices.Clear();
        for (var i = 0; i < cache.Grids.Count; i++)
        {
            if (cache.LivePoses[i] is not { } pose)
                continue;

            foreach (var contour in cache.Grids[i].Contours)
            {
                var count = SunlightProjectionGeometry.Project(contour, pose, cache.Direction, farDepth, _shadow);
                for (var vertex = 1; vertex < count - 1; vertex++)
                {
                    _liveVertices.Add(_shadow[0]);
                    _liveVertices.Add(_shadow[vertex]);
                    _liveVertices.Add(_shadow[vertex + 1]);
                }
            }
        }

        handle.RenderInRenderTarget(cache.LiveMask, () =>
        {
            if (_liveVertices.Count == 0)
                return;

            handle.UseShader(_prototypes.Index(Unshaded).Instance());
            handle.SetTransform(cache.WorldToMask);
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, _liveVertices, Color.Black);
        }, Color.White);

        return cache.LiveMask;
    }

    internal static Matrix3x2? GetGridPose(IEntityManager entities, SharedTransformSystem transform,
        MapSunlightGrid group, EntityUid map)
    {
        if (entities.TryGetEntity(group.Grid, out var grid)
            && entities.TryGetComponent(grid.Value, out TransformComponent? xform) && xform.MapUid is { } gridMap)
            return gridMap == map ? transform.GetWorldMatrix(xform) : null;

        return Matrix3x2.CreateRotation((float) group.Rotation.Theta) * Matrix3x2.CreateTranslation(group.Position);
    }

    private static bool FitsMask(Matrix3x2 mask, Matrix3x2 current, Vector2 offset)
    {
        return (mask with { Translation = current.Translation }) == current
            && offset.X is >= FilterMargin and <= 2 * Padding - FilterMargin
            && offset.Y is >= FilterMargin and <= 2 * Padding - FilterMargin;
    }

    private static Vector2 ToTextureOffset(Vector2 topDown) => new(topDown.X, 2 * Padding - topDown.Y);

    protected override void DisposeBehavior()
    {
        _masks.Dispose();
        base.DisposeBehavior();
    }

    private sealed class ViewportMask : IDisposable
    {
        public readonly List<Box2> Bounds = new();
        public readonly List<Vector2> Vertices = new();
        public readonly List<MapSunlightGrid> Grids = new();
        public MapSunlightSnapshot? Snapshot;
        public IRenderTexture? LiveMask;
        public Matrix3x2?[] LivePoses = [];
        public Matrix3x2 LiveTransform;
        public Vector2 LiveDirection;
        public bool LiveDirty;
        public IRenderTexture? Mask;
        public IRenderTexture? Spare;
        public Matrix3x2 WorldToMask;
        public NetEntity Map;
        public Vector2 Direction;
        public uint Frame;
        public bool Temporal;

        public bool SetLiveGrids(MapSunlightSnapshot snapshot, NetEntity map)
        {
            var partitionChanged = false;
            var i = 0;
            foreach (var grid in snapshot.Grids)
            {
                if (grid.Map != map)
                    continue;

                if (i == Grids.Count)
                {
                    Grids.Add(grid);
                    partitionChanged = true;
                }
                else
                {
                    partitionChanged |= Grids[i].Grid != grid.Grid;
                    LiveDirty |= !Grids[i].SameContours(grid);
                    Grids[i] = grid;
                }

                i++;
            }

            if (i < Grids.Count)
            {
                Grids.RemoveRange(i, Grids.Count - i);
                partitionChanged = true;
            }

            LiveDirty |= partitionChanged;
            return partitionChanged;
        }

        public void SetGeometry(MapSunlightSnapshot snapshot, NetEntity map)
        {
            Map = map;
            Bounds.Clear();
            Vertices.Clear();
            foreach (var region in snapshot.Regions)
            {
                if (region.Map != map)
                    continue;

                Direction = region.Direction;
                Bounds.Add(region.Bounds);
                foreach (var strip in region.Lit)
                {
                    var a = new Vector2(strip.StartAtMin, strip.MinOffset);
                    var b = new Vector2(strip.EndAtMin, strip.MinOffset);
                    var c = new Vector2(strip.EndAtMax, strip.MaxOffset);
                    var d = new Vector2(strip.StartAtMax, strip.MaxOffset);
                    Vertices.Add(a);
                    Vertices.Add(b);
                    Vertices.Add(c);
                    Vertices.Add(a);
                    Vertices.Add(c);
                    Vertices.Add(d);
                }
            }
        }

        public void Dispose()
        {
            Mask?.Dispose();
            Spare?.Dispose();
            LiveMask?.Dispose();
        }
    }
}
