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
    private readonly OverlayResourceCache<ViewportMask> _masks = new();
    private ShaderInstance? _composite;

    public override OverlaySpace Space => OverlaySpace.BeforeLighting;

    public MapSunlightOverlay(PlanetLightSystem lighting)
    {
        IoCManager.InjectDependencies(this);
        _lighting = lighting;
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
        var geometryChanged = !sameMap || cache.Snapshot != snapshot;
        if (geometryChanged)
            cache.SetGeometry(snapshot, map);
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
        var blendPrevious = geometryChanged && sameMap && previousDirection == cache.Direction && fits
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
        var shader = _composite ??= _prototypes.Index(Composite).InstanceUnique();
        shader.SetParameter("CurrentOffset", ToTextureOffset(cache.WorldToMask.Translation - worldToLight.Translation));
        shader.SetParameter("PreviousOffset", ToTextureOffset(previousOffset));
        shader.SetParameter("PreviousMask", blendPrevious ? previous!.Texture : mask.Texture);
        shader.SetParameter("BlendPrevious", blendPrevious);

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
        public MapSunlightSnapshot? Snapshot;
        public IRenderTexture? Mask;
        public IRenderTexture? Spare;
        public Matrix3x2 WorldToMask;
        public NetEntity Map;
        public Vector2 Direction;
        public uint Frame;
        public bool Temporal;

        public void SetGeometry(MapSunlightSnapshot snapshot, NetEntity map)
        {
            Snapshot = snapshot;
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
        }
    }
}
