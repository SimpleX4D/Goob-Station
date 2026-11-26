// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Client.Light;
using Content.Client.Light.EntitySystems;
using Content.Shared.Light;
using Content.Shared.Light.Components;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using ContentCCVars = Content.Shared.CCVar.CCVars;

namespace Content.IntegrationTests.Tests.Station;

[TestFixture]
public sealed class MapSunlightTest
{
    // Sunlight Changes: A narrow wall must keep its shadow after it leaves client PVS, and stable geometry should not be resent.
    [Test]
    public async Task UnloadedWallShadowsRemainStableUntilTheMapChanges()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        var maps = server.System<SharedMapSystem>();
        var lighting = client.System<PlanetLightSystem>();
        EntityUid wall = default;
        NetEntity netWall = default;
        MapSunlightSnapshot? snapshot = null;

        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(CVars.NetPVS, true);
            var viewer = server.EntMan.SpawnAttachedTo(null, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            server.EntMan.AddComponent<EyeComponent>(viewer);
            server.PlayerMan.SetAttachedEntity(server.PlayerMan.Sessions.Single(), viewer);
        });
        await pair.RunTicksSync(18);
        await client.WaitAssertion(() => Assert.That(lighting.Sunlight, Is.Null));

        await server.WaitPost(() =>
        {
            var sunlight = server.EntMan.AddComponent<MapSunlightComponent>(map.MapUid);
            sunlight.StarBearing = (-Vector2.UnitX).ToWorldAngle();
            server.EntMan.Dirty(map.MapUid, sunlight);

            var distant = maps.CreateGridEntity(map.MapId);
            maps.SetTile(distant, Vector2i.Zero, map.Tile.Tile);
            server.System<SharedTransformSystem>().SetWorldPosition(distant, new Vector2(-64f, 0.14f));
            wall = server.EntMan.SpawnAttachedTo("WallDirectional", new EntityCoordinates(distant, 0.5f, 0.5f));
            netWall = server.EntMan.GetNetEntity(wall);
        });
        await pair.RunTicksSync(24);
        await client.WaitAssertion(() =>
        {
            Assert.That(client.EntMan.TryGetEntity(netWall, out _), Is.False);
            snapshot = lighting.Sunlight;
            Assert.That(snapshot, Is.Not.Null);
            var region = snapshot!.Regions.Single();
            Assert.That(IsLit(region, new Vector2(0.5f, 0.18f)), Is.False);
            Assert.That(IsLit(region, new Vector2(0.5f, 0.10f)), Is.True);
        });

        await pair.RunTicksSync(18);
        await client.WaitAssertion(() => Assert.That(lighting.Sunlight, Is.SameAs(snapshot)));

        await server.WaitPost(() => server.EntMan.DeleteEntity(wall));
        await pair.RunTicksSync(18);
        await client.WaitAssertion(() =>
        {
            Assert.That(lighting.Sunlight, Is.Not.SameAs(snapshot));
            Assert.That(IsLit(lighting.Sunlight!.Regions.Single(), new Vector2(0.5f, 0.18f)), Is.True);
            Assert.That(client.CfgMan.IsCVarRegistered(ContentCCVars.StationSunlightTemporal.Name), Is.True);
        });

        await server.WaitPost(() =>
        {
            Assert.That(server.CfgMan.IsCVarRegistered(ContentCCVars.StationSunlightTemporal.Name), Is.False);
            server.CfgMan.SetCVar(ContentCCVars.StationSunlightRender, false);
        });
        await pair.RunTicksSync(12);
        await client.WaitAssertion(() => Assert.That(lighting.Sunlight, Is.Null));

        await server.WaitPost(() => server.CfgMan.SetCVar(ContentCCVars.StationSunlightRender, true));
        await pair.RunTicksSync(12);
        await client.WaitAssertion(() => Assert.That(lighting.Sunlight, Is.Not.Null));
        await pair.CleanReturnAsync();
    }

    // Sunlight Changes: Changing only the client pose must move a shadow before another server snapshot arrives.
    [Test]
    public async Task ShuttleShadowTracksClientRotationBetweenSnapshots()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        var maps = server.System<SharedMapSystem>();
        var serverTransforms = server.System<SharedTransformSystem>();
        var clientTransforms = client.System<SharedTransformSystem>();
        var lighting = client.System<PlanetLightSystem>();
        NetEntity netGrid = default;
        NetEntity netMap = default;

        await server.WaitPost(() =>
        {
            var sun = server.EntMan.AddComponent<MapSunlightComponent>(map.MapUid);
            sun.StarBearing = (-Vector2.UnitX).ToWorldAngle();
            server.EntMan.Dirty(map.MapUid, sun);
            var viewer = server.EntMan.SpawnAttachedTo(null, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            server.EntMan.AddComponent<EyeComponent>(viewer);
            server.PlayerMan.SetAttachedEntity(server.PlayerMan.Sessions.Single(), viewer);

            var shuttle = maps.CreateGridEntity(map.MapId);
            maps.SetTile(shuttle, new Vector2i(2, 0), map.Tile.Tile);
            serverTransforms.SetWorldPosition(shuttle, new Vector2(-6, 0));
            server.EntMan.SpawnAttachedTo("WallSolid", new EntityCoordinates(shuttle, 2.5f, 0.5f));
            netGrid = server.EntMan.GetNetEntity(shuttle.Owner);
            netMap = server.EntMan.GetNetEntity(map.MapUid);
        });
        await pair.RunTicksSync(16);

        await client.WaitAssertion(() =>
        {
            var snapshot = lighting.Sunlight!;
            var region = snapshot.Regions.Single();
            var group = snapshot.Grids.Single(grid => grid.Grid == netGrid);
            Assert.That(IsLit(region, new Vector2(0.5f, 0.5f)), Is.True,
                "The live shuttle must be excluded from the baked mask.");

            var gridUid = client.EntMan.GetEntity(netGrid);
            var mapUid = client.EntMan.GetEntity(netMap);
            var contour = group.Contours.Single();
            var projected = new Vector2[SunlightProjectionGeometry.MaxVertices];

            var before = MapSunlightOverlay.GetGridPose(client.EntMan, clientTransforms, group, mapUid)!.Value;
            var count = SunlightProjectionGeometry.Project(contour, before, region.Direction, 10f, projected);
            Assert.That(count, Is.GreaterThanOrEqualTo(3));
            var initialBottom = projected.AsSpan(0, count).ToArray().Min(p => p.Y);

            clientTransforms.SetLocalRotation(gridUid, Angle.FromDegrees(90));
            var after = MapSunlightOverlay.GetGridPose(client.EntMan, clientTransforms, group, mapUid)!.Value;
            count = SunlightProjectionGeometry.Project(contour, after, region.Direction, 10f, projected);
            Assert.That(count, Is.GreaterThanOrEqualTo(3));
            Assert.That(projected.AsSpan(0, count).ToArray().Min(p => p.Y), Is.GreaterThan(initialBottom + 1.5f));
            Assert.That(lighting.Sunlight, Is.SameAs(snapshot));
        });
        await pair.CleanReturnAsync();
    }

    private static bool IsLit(MapSunlightRegion region, Vector2 point)
    {
        var across = new Vector2(-region.Direction.Y, region.Direction.X);
        var offset = Vector2.Dot(point, across);
        var depth = Vector2.Dot(point, region.Direction);
        foreach (var strip in region.Lit)
        {
            if (offset < strip.MinOffset || offset >= strip.MaxOffset)
                continue;

            var fraction = (offset - strip.MinOffset) / (strip.MaxOffset - strip.MinOffset);
            var near = float.Lerp(strip.StartAtMin, strip.StartAtMax, fraction);
            var far = float.Lerp(strip.EndAtMin, strip.EndAtMax, fraction);
            if (depth >= near && depth < far)
                return true;
        }

        return false;
    }
}
