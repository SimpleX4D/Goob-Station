// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Shared.CCVar;
using Content.Shared.Light;
using Content.Shared.Light.Components;
using Content.Shared.Physics;
using Content.Shared.Tag;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
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
    private const float MaxMovingGridSide = 96f;
    private const int MaxMovingGrids = 8;
    private const int MaxMovingPolygons = 256;
    private static readonly TimeSpan MotionHold = TimeSpan.FromSeconds(1);
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private static readonly MapSunlightSnapshot ClearSnapshot = new([], []);

    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<NetEntity, MapGeometry> _maps = new();
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<ICommonSession, MapSunlightSnapshot> _sent = new();
    private readonly Dictionary<EntityUid, TimeSpan> _movingUntil = new();
    private readonly Dictionary<NetEntity, int> _liveByMap = new();
    private readonly List<(ICommonSession Session, List<View> Views)> _recipients = new();
    private readonly List<SunlightSegment> _edges = new();
    private float _elapsed = Interval;

    public override void Initialize()
    {
        base.Initialize();
        _transform.OnGlobalMoveEvent += OnGridMoved;
        // Sunlight Changes: Force a fresh snapshot after a live disable clears the renderer on each client.
        Subs.CVar(_cfg, CCVars.StationSunlightRender, _ =>
        {
            _movingUntil.Clear();
            _sent.Clear();
            _elapsed = Interval;
        }, true);
    }

    public override void Shutdown()
    {
        _transform.OnGlobalMoveEvent -= OnGridMoved;
        base.Shutdown();
    }

    private void OnGridMoved(ref MoveEvent args)
    {
        if (args.Component.GridUid == args.Sender && !args.ParentChanged
            && (args.OldPosition != args.NewPosition || args.OldRotation != args.NewRotation)
            && _cfg.GetCVar(CCVars.StationSunlightRender))
            _movingUntil[args.Sender] = _timing.CurTime + MotionHold;
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
        foreach (var (grid, until) in _movingUntil)
        {
            if (until <= _timing.CurTime || Deleted(grid))
                _movingUntil.Remove(grid);
        }
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
            geometry.Views.Add(bounds);
            return;
        }

        var direction = -sun.StarBearing.ToWorldVec();
        var worldToSun = new Matrix3x2(direction.X, -direction.Y, direction.Y, direction.X, 0f, 0f);
        var sample = new MapGeometry(netMap, xform.MapID, direction, worldToSun, worldToSun.TransformBox(bounds));
        sample.Views.Add(bounds);
        _maps.Add(netMap, sample);
    }

    private void PrepareEnvelope(MapGeometry map)
    {
        _edges.Clear();
        var query = new ContourQuery(this, map.Bounds, _edges);
        foreach (var grid in _map.GetAllGrids(map.MapId))
        {
            if (!TryComp<BroadphaseComponent>(grid.Owner, out var broadphase))
                continue;

            var toWorld = _transform.GetWorldMatrix(grid.Owner);
            var toSun = toWorld * map.WorldToSun;
            var localBounds = grid.Comp.LocalAABB.Enlarged(GridPadding);
            var bounds = toSun.TransformBox(localBounds);
            if (bounds.Left >= map.Bounds.Right || bounds.Bottom >= map.Bounds.Top || bounds.Top <= map.Bounds.Bottom)
                continue;

            if (TryCaptureGrid(map, grid.Owner, broadphase, localBounds, toWorld, toSun))
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
        if (!system.IsOpaque(proxy, out var xform))
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

    // Sunlight Changes: Share the fixture filter so a shadow does not change material when its grid starts moving.
    private bool IsOpaque(in FixtureProxy proxy,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TransformComponent? xform)
    {
        xform = null;
        return proxy.Fixture.Hard && (proxy.Fixture.CollisionLayer & (int) CollisionGroup.Opaque) != 0
            && TryComp(proxy.Entity, out xform) && xform.Anchored && BlocksLight(proxy.Entity);
    }

    // Sunlight Changes: Diagonal walls may omit the engine occluder even though their physics shape still blocks daylight.
    private bool BlocksLight(EntityUid uid) => _tags.HasTag(uid, WallTag)
        || TryComp<OccluderComponent>(uid, out var occluder) && occluder.Enabled;

    private static void AddFrontEdge(ref ContourQuery query, Vector2 a, Vector2 b)
    {
        if (a.Y > b.Y && Math.Min(a.X, b.X) < query.Bounds.Right && a.Y > query.Bounds.Bottom && b.Y < query.Bounds.Top)
            query.Edges.Add(SunlightSegment.FromPoints(a, b));
    }

    private bool TryCaptureGrid(MapGeometry map, EntityUid uid, BroadphaseComponent broadphase,
        Box2 localBounds, Matrix3x2 toWorld, Matrix3x2 toSun)
    {
        var budget = MaxMovingPolygons - map.MovingPolygons;
        if (map.Grids.Count == MaxMovingGrids || budget == 0
            || localBounds.Width > MaxMovingGridSide || localBounds.Height > MaxMovingGridSide || !CanMove(uid))
            return false;

        var worldBounds = toWorld.TransformBox(localBounds);
        if (!map.Views.Exists(view => view.Intersects(worldBounds)))
            return false;

        // Sunlight Changes: Gather the entire local contour so a rotation can reveal edges absent from the current silhouette.
        var capture = new GridCapture(this, budget);
        broadphase.StaticTree.QueryAabb(ref capture, CollectLocalFixture, localBounds, approx: true);
        if (!capture.Incomplete)
            broadphase.DynamicTree.QueryAabb(ref capture, CollectLocalFixture, localBounds, approx: true);
        if (capture.Incomplete || capture.Contours.Count == 0)
            return false;

        var rotation = new Angle(Math.Atan2(toWorld.M12, toWorld.M11));
        var data = new MapSunlightGrid(map.NetMap, GetNetEntity(uid), toWorld.Translation, rotation, capture.Contours.ToArray());
        map.Grids.Add(new MovingGrid(data, worldBounds, toSun));
        map.MovingPolygons += data.Contours.Length;
        return true;
    }

    private bool CanMove(EntityUid uid)
    {
        if (_movingUntil.ContainsKey(uid) || TryComp(uid, out PhysicsComponent? body)
            && (Math.Abs(body.AngularVelocity) > 0.001f || body.LinearVelocity.LengthSquared() > 0.0001f))
            return true;

        // Sunlight Changes: Prepare enabled shuttles before they turn to avoid waiting a snapshot when thrust starts.
        return TryComp(uid, out ShuttleComponent? shuttle) && shuttle.Enabled
            && (shuttle.AngularThrust > 0f || Array.Exists(shuttle.LinearThrust, force => force > 0f));
    }

    private static bool CollectLocalFixture(ref GridCapture capture, in FixtureProxy proxy)
    {
        if (!capture.System.IsOpaque(proxy, out var xform))
            return true;

        if (proxy.Fixture.Shape is not PolygonShape { VertexCount: >= 3 } polygon
            || polygon.VertexCount + 2 > SunlightProjectionGeometry.MaxVertices || capture.Contours.Count >= capture.Budget)
        {
            capture.Incomplete = true;
            return false;
        }

        var pose = capture.System._physics.GetLocalPhysicsTransform(proxy.Entity, xform);
        var local = new Vector2[polygon.VertexCount];
        for (var i = 0; i < local.Length; i++)
            local[i] = PhysicsTransform.Mul(pose, polygon.Vertices[i]);
        capture.Contours.Add(local);
        return true;
    }

    private void SendChangedViews(ICommonSession session, List<View> views)
    {
        _liveByMap.Clear();
        List<MapSunlightGrid>? grids = null;
        foreach (var view in views)
        {
            if (!_liveByMap.TryAdd(view.Map, 0))
                continue;

            var map = _maps[view.Map];
            for (var i = 0; i < map.Grids.Count; i++)
            {
                var grid = map.Grids[i];
                if (!views.Exists(other => other.Map == view.Map && other.Bounds.Intersects(grid.Bounds)))
                    continue;

                _liveByMap[view.Map] |= 1 << i;
                (grids ??= []).Add(grid.Data);
            }
        }

        var regions = new MapSunlightRegion[views.Count];
        Span<SunlightEnvelope.ViewSlice> slices = stackalloc SunlightEnvelope.ViewSlice[3];
        for (var i = 0; i < regions.Length; i++)
        {
            var view = views[i];
            var map = _maps[view.Map];
            var live = _liveByMap[view.Map];
            var key = (view.Bounds, live);
            if (!map.Regions.TryGetValue(key, out var region))
            {
                var count = SunlightEnvelope.GetViewSlices(view.Bounds, map.WorldToSun, slices);
                var lit = new List<SunlightTrapezoid>();
                SunlightEnvelope.ClipEnvelope(GetEnvelope(map, live), slices[..count], lit);
                region = new MapSunlightRegion(view.Map, view.Bounds, map.Direction, lit.ToArray());
                map.Regions.Add(key, region);
            }

            regions[i] = region;
        }

        // Sunlight Changes: Send changed geometry reliably and leave identical masks and mesh buffers valid on the client.
        var snapshot = new MapSunlightSnapshot(regions, grids?.ToArray() ?? []);
        if (_sent.TryGetValue(session, out var previous) && previous.SameRegions(snapshot) && previous.SameGrids(snapshot))
            return;

        _sent[session] = snapshot;
        RaiseNetworkEvent(snapshot, session);
    }

    // Sunlight Changes: Bake distant grids for this recipient so their shadows survive PVS culling without a live layer.
    private static List<SunlightSegment> GetEnvelope(MapGeometry map, int live)
    {
        if (live == (1 << map.Grids.Count) - 1)
            return map.Envelope;

        if (map.Baked.TryGetValue(live, out var baked))
            return baked;

        var edges = new List<SunlightSegment>(map.Envelope);
        for (var i = 0; i < map.Grids.Count; i++)
        {
            if ((live & (1 << i)) != 0)
                continue;

            var grid = map.Grids[i];
            foreach (var contour in grid.Data.Contours)
            {
                var previous = Vector2.Transform(contour[^1], grid.ToSun);
                foreach (var point in contour)
                {
                    var next = Vector2.Transform(point, grid.ToSun);
                    if (previous.Y > next.Y)
                        edges.Add(SunlightSegment.FromPoints(previous, next));
                    previous = next;
                }
            }
        }

        baked = SunlightEnvelope.Build(edges);
        map.Baked.Add(live, baked);
        return baked;
    }

    private readonly record struct View(NetEntity Map, Box2 Bounds);

    private sealed class MapGeometry(NetEntity netMap, MapId mapId, Vector2 direction, Matrix3x2 worldToSun, Box2 bounds)
    {
        public readonly NetEntity NetMap = netMap;
        public readonly MapId MapId = mapId;
        public readonly Vector2 Direction = direction;
        public readonly Matrix3x2 WorldToSun = worldToSun;
        public readonly List<Box2> Views = new();
        public readonly List<MovingGrid> Grids = new();
        public readonly Dictionary<(Box2, int), MapSunlightRegion> Regions = new();
        public readonly Dictionary<int, List<SunlightSegment>> Baked = new();
        public int MovingPolygons;
        public Box2 Bounds = bounds;
        public List<SunlightSegment> Envelope = [];
    }

    private readonly record struct MovingGrid(MapSunlightGrid Data, Box2 Bounds, Matrix3x2 ToSun);

    private struct GridCapture(MapSunlightSystem system, int budget)
    {
        public readonly MapSunlightSystem System = system;
        public readonly int Budget = budget;
        public readonly List<Vector2[]> Contours = new();
        public bool Incomplete;
    }

    private struct ContourQuery(MapSunlightSystem system, Box2 bounds, List<SunlightSegment> edges)
    {
        public readonly MapSunlightSystem System = system;
        public readonly Box2 Bounds = bounds;
        public readonly List<SunlightSegment> Edges = edges;
        public Matrix3x2 GridToSun;
    }
}
