// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.CCVar;
using Content.Shared.Light;
using Content.Shared.Light.Components;
using Content.Shared.Physics;
using Content.Shared.Tag;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using PhysicsTransform = Robust.Shared.Physics.Transform;

namespace Content.Server.Light.EntitySystems;

// Sunlight Changes: Share one opaque shadow envelope across the active views of a map.
public sealed class MapSunlightSystem : EntitySystem
{
    private const float Interval = 0.125f;
    private const float ViewStep = 8f;
    private const float ViewPadding = 8f;
    private const float MaxViewRadius = 64f;
    private const float GridPadding = 2f;
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private static readonly MapSunlightSnapshot ClearSnapshot = new([]);

    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<NetEntity, MapGeometry> _maps = new();
    private readonly Dictionary<ICommonSession, MapSunlightRegion[]> _sent = new();
    private readonly List<(ICommonSession Session, List<View> Views)> _recipients = new();
    private readonly List<SunlightSegment> _edges = new();
    private float _elapsed = Interval;

    public override void Initialize()
    {
        base.Initialize();
        // Sunlight Changes: Force a fresh snapshot after a live disable clears the renderer on each client.
        Subs.CVar(_cfg, CCVars.StationSunlightRender, _ =>
        {
            _sent.Clear();
            _elapsed = Interval;
        }, true);
    }

    public override void Update(float frameTime)
    {
        if (!_cfg.GetCVar(CCVars.StationSunlightRender))
            return;

        _elapsed += frameTime;
        if (_elapsed < Interval)
            return;

        _elapsed %= Interval;
        _maps.Clear();
        _recipients.Clear();
        foreach (var session in _sent.Keys)
        {
            if (session.Status != SessionStatus.InGame)
                _sent.Remove(session);
        }

        var radius = Math.Max(_cfg.GetCVar(CCVars.NetMaxUpdateRange), _cfg.GetCVar(CCVars.NetPvsPriorityRange)) / 2f;
        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame)
                continue;

            List<View>? views = null;
            if (session.AttachedEntity is { } viewer)
                AddView(viewer, radius, ref views);
            foreach (var camera in session.ViewSubscriptions)
            {
                if (camera != session.AttachedEntity)
                    AddView(camera, radius, ref views);
            }

            if (views != null)
                _recipients.Add((session, views));
            else if (_sent.Remove(session))
                RaiseNetworkEvent(ClearSnapshot, session);
        }

        foreach (var geometry in _maps.Values)
            PrepareEnvelope(geometry);

        foreach (var (session, views) in _recipients)
            SendChangedViews(session, views);
    }

    private void AddView(EntityUid viewer, float radius, ref List<View>? views)
    {
        if (!TryComp(viewer, out TransformComponent? xform) || xform.MapUid is not { } map
            || !TryComp<MapSunlightComponent>(map, out var sun) || !sun.Enabled
            || !double.IsFinite(sun.StarBearing.Theta))
            return;

        var center = _transform.GetWorldPosition(xform);
        if (TryComp<EyeComponent>(viewer, out var eye))
        {
            center += eye.Offset;
            radius *= eye.PvsScale;
        }

        // Sunlight Changes: Keep the receiver fixed through small camera moves so the client can retain its mesh and mask.
        center = new Vector2(MathF.Round(center.X / ViewStep), MathF.Round(center.Y / ViewStep)) * ViewStep;
        radius = Math.Clamp(radius + ViewPadding, ViewPadding, MaxViewRadius);
        var bounds = Box2.CenteredAround(center, new Vector2(radius * 2f));
        var netMap = GetNetEntity(map);
        var view = new View(netMap, bounds);
        views ??= new List<View>();
        if (views.Contains(view))
            return;

        views.Add(view);
        if (_maps.TryGetValue(netMap, out var geometry))
        {
            geometry.Bounds = geometry.Bounds.Union(geometry.WorldToSun.TransformBox(bounds));
            return;
        }

        var direction = -sun.StarBearing.ToWorldVec();
        var worldToSun = new Matrix3x2(direction.X, -direction.Y, direction.Y, direction.X, 0f, 0f);
        _maps.Add(netMap, new MapGeometry(xform.MapID, direction, worldToSun, worldToSun.TransformBox(bounds)));
    }

    private void PrepareEnvelope(MapGeometry map)
    {
        _edges.Clear();
        var query = new ContourQuery(this, map.Bounds, _edges);
        foreach (var grid in _map.GetAllGrids(map.MapId))
        {
            if (!TryComp<BroadphaseComponent>(grid.Owner, out var broadphase))
                continue;

            var toSun = _transform.GetWorldMatrix(grid.Owner) * map.WorldToSun;
            var bounds = toSun.TransformBox(grid.Comp.LocalAABB.Enlarged(GridPadding));
            if (bounds.Left >= map.Bounds.Right || bounds.Bottom >= map.Bounds.Top || bounds.Top <= map.Bounds.Bottom)
                continue;

            // Sunlight Changes: Retain upstream light blockers even when their grid is outside the client's view.
            var overlap = new Box2(bounds.Left, Math.Max(bounds.Bottom, map.Bounds.Bottom),
                Math.Min(bounds.Right, map.Bounds.Right), Math.Min(bounds.Top, map.Bounds.Top));
            Matrix3x2.Invert(toSun, out var toGrid);
            var local = toGrid.TransformBox(overlap);
            query.GridToSun = toSun;
            broadphase.StaticTree.QueryAabb(ref query, CollectFixture, local, approx: true);
            broadphase.DynamicTree.QueryAabb(ref query, CollectFixture, local, approx: true);
        }

        map.Envelope = SunlightEnvelope.Build(_edges);
    }

    private static bool CollectFixture(ref ContourQuery query, in FixtureProxy proxy)
    {
        var fixture = proxy.Fixture;
        var system = query.System;
        // Sunlight Changes: Use fixture dimensions for narrow walls and doors while respecting their live occlusion state.
        if (!fixture.Hard || (fixture.CollisionLayer & (int) CollisionGroup.Opaque) == 0
            || !system.TryComp(proxy.Entity, out TransformComponent? xform) || !xform.Anchored
            || !system.BlocksLight(proxy.Entity))
            return true;

        var pose = system._physics.GetLocalPhysicsTransform(proxy.Entity, xform);
        if (fixture.Shape is PolygonShape { VertexCount: >= 3 } polygon)
        {
            var previous = Vector2.Transform(PhysicsTransform.Mul(pose, polygon.Vertices[polygon.VertexCount - 1]), query.GridToSun);
            for (var i = 0; i < polygon.VertexCount; i++)
            {
                var point = Vector2.Transform(PhysicsTransform.Mul(pose, polygon.Vertices[i]), query.GridToSun);
                AddFrontEdge(ref query, previous, point);
                previous = point;
            }
        }
        else if (fixture.Shape is PhysShapeCircle circle)
        {
            var center = Vector2.Transform(PhysicsTransform.Mul(pose, circle.Position), query.GridToSun);
            var previous = center + Vector2.UnitY * circle.Radius;
            const int segments = 12;
            for (var i = 1; i <= segments; i++)
            {
                var (sin, cos) = MathF.SinCos(MathF.PI * i / segments);
                var point = center + new Vector2(-sin, cos) * circle.Radius;
                AddFrontEdge(ref query, previous, point);
                previous = point;
            }
        }

        return true;
    }

    // Sunlight Changes: Diagonal walls may omit the engine occluder even though their physics shape still blocks daylight.
    private bool BlocksLight(EntityUid uid) => _tags.HasTag(uid, WallTag)
        || TryComp<OccluderComponent>(uid, out var occluder) && occluder.Enabled;

    private static void AddFrontEdge(ref ContourQuery query, Vector2 a, Vector2 b)
    {
        if (a.Y > b.Y && Math.Min(a.X, b.X) < query.Bounds.Right && a.Y > query.Bounds.Bottom && b.Y < query.Bounds.Top)
            query.Edges.Add(SunlightSegment.FromPoints(a, b));
    }

    private void SendChangedViews(ICommonSession session, List<View> views)
    {
        var regions = new MapSunlightRegion[views.Count];
        Span<SunlightEnvelope.ViewSlice> slices = stackalloc SunlightEnvelope.ViewSlice[3];
        for (var i = 0; i < regions.Length; i++)
        {
            var view = views[i];
            var map = _maps[view.Map];
            if (!map.Regions.TryGetValue(view.Bounds, out var region))
            {
                var count = SunlightEnvelope.GetViewSlices(view.Bounds, map.WorldToSun, slices);
                var lit = new List<SunlightTrapezoid>();
                SunlightEnvelope.ClipEnvelope(map.Envelope, slices[..count], lit);
                region = new MapSunlightRegion(view.Map, view.Bounds, map.Direction, lit.ToArray());
                map.Regions.Add(view.Bounds, region);
            }

            regions[i] = region;
        }

        // Sunlight Changes: Send changed geometry reliably and leave identical masks and mesh buffers valid on the client.
        if (_sent.TryGetValue(session, out var previous) && SameGeometry(previous, regions))
            return;

        _sent[session] = regions;
        RaiseNetworkEvent(new MapSunlightSnapshot(regions), session);
    }

    private static bool SameGeometry(MapSunlightRegion[] previous, MapSunlightRegion[] current)
    {
        if (previous.Length != current.Length)
            return false;

        for (var i = 0; i < current.Length; i++)
        {
            var left = previous[i];
            var right = current[i];
            if (left.Map != right.Map || left.Direction != right.Direction || left.Bounds != right.Bounds
                || !left.Lit.AsSpan().SequenceEqual(right.Lit))
                return false;
        }

        return true;
    }

    private readonly record struct View(NetEntity Map, Box2 Bounds);

    private sealed class MapGeometry(MapId mapId, Vector2 direction, Matrix3x2 worldToSun, Box2 bounds)
    {
        public readonly MapId MapId = mapId;
        public readonly Vector2 Direction = direction;
        public readonly Matrix3x2 WorldToSun = worldToSun;
        public readonly Dictionary<Box2, MapSunlightRegion> Regions = new();
        public Box2 Bounds = bounds;
        public List<SunlightSegment> Envelope = [];
    }

    private struct ContourQuery(MapSunlightSystem system, Box2 bounds, List<SunlightSegment> edges)
    {
        public readonly MapSunlightSystem System = system;
        public readonly Box2 Bounds = bounds;
        public readonly List<SunlightSegment> Edges = edges;
        public Matrix3x2 GridToSun;
    }
}
