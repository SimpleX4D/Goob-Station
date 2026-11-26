// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.CCVar;
using Content.Shared.Light;
using Robust.Client;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;

namespace Content.Client.Light.EntitySystems;

public sealed class PlanetLightSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfgManager = default!;
    [Dependency] private readonly IOverlayManager _overlayMan = default!;

    /// <summary>
    /// Enables / disables the ambient occlusion overlay.
    /// </summary>
    public bool AmbientOcclusion
    {
        get => _ambientOcclusion;
        set
        {
            if (_ambientOcclusion == value)
                return;

            _ambientOcclusion = value;

            if (value)
            {
                _overlayMan.AddOverlay(new AmbientOcclusionOverlay());
            }
            else
            {
                _overlayMan.RemoveOverlay<AmbientOcclusionOverlay>();
            }
        }
    }

    private bool _ambientOcclusion;

    // Sunlight Changes: Keep only the active connection's receiver geometry.
    [Dependency] private IBaseClient _client = default!;
    internal MapSunlightSnapshot? Sunlight { get; private set; }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GetClearColorEvent>(OnClearColor);
        SubscribeNetworkEvent<MapSunlightSnapshot>(OnSunlightSnapshot);
        _client.RunLevelChanged += OnRunLevelChanged;

        _cfgManager.OnValueChanged(CCVars.AmbientOcclusion, val =>
        {
            AmbientOcclusion = val;
        }, true);

        _overlayMan.AddOverlay(new BeforeLightTargetOverlay());
        _overlayMan.AddOverlay(new RoofOverlay(EntityManager));
        _overlayMan.AddOverlay(new TileEmissionOverlay(EntityManager));
        _overlayMan.AddOverlay(new LightBlurOverlay());
        _overlayMan.AddOverlay(new SunShadowOverlay());
        _overlayMan.AddOverlay(new AfterLightTargetOverlay());
        // Sunlight Changes: Composite direct sunlight after ambient, roof and tile lighting are in the light target.
        Subs.CVar(_cfgManager, CCVars.StationSunlightRender, SetSunlightRender, true);
    }

    // Sunlight Changes: Remove the overlay on a server-wide disable so render history cannot survive a policy change.
    private void SetSunlightRender(bool enabled)
    {
        if (enabled)
        {
            if (!_overlayMan.HasOverlay<MapSunlightOverlay>())
                _overlayMan.AddOverlay(new MapSunlightOverlay(this));
        }
        else
        {
            Sunlight = null;
            if (_overlayMan.TryGetOverlay<MapSunlightOverlay>(out var overlay))
            {
                _overlayMan.RemoveOverlay(overlay);
                overlay.Dispose();
            }
        }
    }

    private void OnSunlightSnapshot(MapSunlightSnapshot snapshot)
    {
        Sunlight = snapshot.Regions.Length > 0 ? snapshot : null;
    }

    private void OnRunLevelChanged(object? sender, RunLevelChangedEventArgs ev)
    {
        if (ev.NewLevel != ClientRunLevel.InGame)
            Sunlight = null;
    }

    private void OnClearColor(ref GetClearColorEvent ev)
    {
        ev.Color = Color.Transparent;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlayMan.RemoveOverlay<BeforeLightTargetOverlay>();
        _overlayMan.RemoveOverlay<RoofOverlay>();
        _overlayMan.RemoveOverlay<TileEmissionOverlay>();
        _overlayMan.RemoveOverlay<LightBlurOverlay>();
        _overlayMan.RemoveOverlay<SunShadowOverlay>();
        _overlayMan.RemoveOverlay<AfterLightTargetOverlay>();
        _client.RunLevelChanged -= OnRunLevelChanged;
        // Sunlight Changes: Release both frame masks when the client entity systems shut down.
        SetSunlightRender(false);
    }
}