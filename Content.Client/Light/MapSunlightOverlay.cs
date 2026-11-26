// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using Content.Client.Graphics;
using Content.Shared.CCVar;
using Content.Shared.Light.Components;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.Light;

// Sunlight Changes: Reuse the client's wall and door occluders so maps need no extra structure prototypes.
internal sealed class MapSunlightOverlay : Overlay
{
    private const float UpstreamRange = 24f;
    private static readonly ProtoId<ShaderPrototype> Unshaded = "unshaded";
    private static readonly ProtoId<ShaderPrototype> Composite = "MapSunlight";

    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private readonly OverlayResourceCache<ViewportMasks> _masks = new();
    private readonly List<Entity<OccluderComponent, TransformComponent>> _occluders = new();
    private readonly List<Vector2> _triangles = new();
    private ShaderInstance? _composite;

    public override OverlaySpace Space => OverlaySpace.BeforeLighting;

    public MapSunlightOverlay()
    {
        IoCManager.InjectDependencies(this);
        ZIndex = AfterLightTargetOverlay.ContentZIndex + 2;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var viewport = args.Viewport;
        if (viewport.Eye is not { } eye
            || !_entities.TryGetComponent(args.MapUid, out MapSunlightComponent? sun) || !sun.Enabled
            || !double.IsFinite(sun.StarBearing.Theta))
            return;

        var target = viewport.LightRenderTarget;
        var scale = viewport.RenderScale * (target.Size / (Vector2) viewport.Size);
        var worldToMask = target.GetWorldToLocalMatrix(eye, scale);
        var temporal = _cfg.GetCVar(CCVars.StationSunlightTemporal);
        var history = _masks.GetForViewport(viewport, static _ => new ViewportMasks());
        if (temporal)
            (history.Current, history.Previous) = (history.Previous, history.Current);
        else if (history.Previous != null)
        {
            history.Previous.Dispose();
            history.Previous = null;
        }

        if (history.Current?.Size != target.Size)
        {
            history.Current?.Dispose();
            history.Current = _clyde.CreateRenderTarget(target.Size, new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8Srgb),
                new TextureSampleParameters { Filter = true }, name: "map-sunlight-mask");
        }

        var mask = history.Current;
        var direction = -sun.StarBearing.ToWorldVec();
        BuildShadows(args, direction);
        var handle = args.WorldHandle;
        var shader = _composite ??= _prototypes.Index(Composite).InstanceUnique();

        // Sunlight Changes: Jitter raw samples by opposite quarter-pixel offsets instead of accumulating a blurred resolve.
        var jitter = temporal ? ((_timing.CurFrame & 1) == 0 ? -0.25f : 0.25f) : 0f;
        handle.RenderInRenderTarget(mask, () =>
        {
            handle.SetTransform(worldToMask * Matrix3x2.CreateTranslation(jitter, jitter));
            handle.UseShader(_prototypes.Index(Unshaded).Instance());
            if (_triangles.Count > 0)
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, CollectionsMarshal.AsSpan(_triangles), Color.Black);
        }, Color.White);

        // Sunlight Changes: Reproject small camera translations, but drop stale masks after zooms, rotation, teleports or skipped frames.
        var previousDelta = history.Transform.Translation - worldToMask.Translation;
        var blend = temporal && history.Previous?.Size == target.Size && history.Map == args.MapUid
                    && unchecked(history.Frame + 1) == _timing.CurFrame && history.Temporal
                    && history.Direction == direction && previousDelta.LengthSquared() <= 64f
                    && (history.Transform with { Translation = worldToMask.Translation }) == worldToMask;

        shader.SetParameter("PreviousMask", blend ? history.Previous!.Texture : mask.Texture);
        shader.SetParameter("PreviousOffset", new Vector2(previousDelta.X, -previousDelta.Y) / (Vector2) target.Size);
        shader.SetParameter("BlendPrevious", blend);
        var bounds = args.WorldBounds;
        var color = sun.Color;
        handle.RenderInRenderTarget(target, () =>
        {
            handle.SetTransform(worldToMask);
            handle.UseShader(shader);
            handle.DrawTextureRect(mask.Texture, bounds, color);
        }, null);

        history.Transform = worldToMask;
        history.Map = args.MapUid;
        history.Direction = direction;
        history.Temporal = temporal;
        history.Frame = _timing.CurFrame;
    }

    private void BuildShadows(in OverlayDrawArgs args, Vector2 direction)
    {
        // Sunlight Changes: Search upstream only within the nearby client scene to keep the pass independent of server geometry.
        var view = args.WorldAABB;
        var upstream = -direction * UpstreamRange;
        var bounds = new Box2(Vector2.Min(view.BottomLeft, view.BottomLeft + upstream),
            Vector2.Max(view.TopRight, view.TopRight + upstream));
        var projection = direction * (view.Size.Length() + UpstreamRange);
        _occluders.Clear();
        _triangles.Clear();
        _entities.System<OccluderSystem>().QueryAabb(_occluders, args.MapId, bounds);
        var transforms = _entities.System<SharedTransformSystem>();
        Span<Vector2> points = stackalloc Vector2[8];

        foreach (var occluder in _occluders)
        {
            if (!occluder.Comp1.Enabled)
                continue;

            var box = occluder.Comp1.BoundingBox;
            var world = transforms.GetWorldMatrix(occluder.Comp2);
            points[0] = Vector2.Transform(box.BottomLeft, world);
            points[1] = Vector2.Transform(box.BottomRight, world);
            points[2] = Vector2.Transform(box.TopRight, world);
            points[3] = Vector2.Transform(box.TopLeft, world);
            for (var i = 0; i < 4; i++)
                points[i + 4] = points[i] + projection;

            // Sunlight Changes: Fill the convex extrusion once so rotated walls still cast contiguous shadows.
            var hull = PhysicsHull.ComputePoints(points, points.Length);
            for (var i = 1; i < hull.Length - 1; i++)
            {
                _triangles.Add(hull[0]);
                _triangles.Add(hull[i]);
                _triangles.Add(hull[i + 1]);
            }
        }
    }

    protected override void DisposeBehavior()
    {
        _masks.Dispose();
        base.DisposeBehavior();
    }

    private sealed class ViewportMasks : IDisposable
    {
        public IRenderTexture? Current;
        public IRenderTexture? Previous;
        public Matrix3x2 Transform;
        public EntityUid Map;
        public Vector2 Direction;
        public bool Temporal;
        public uint Frame;

        public void Dispose()
        {
            Current?.Dispose();
            Previous?.Dispose();
        }
    }
}
