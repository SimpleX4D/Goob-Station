using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    // Sunlight Changes: Let the server turn off the render pass for every map without changing saved map components.
    public static readonly CVarDef<bool> StationSunlightRender =
        CVarDef.Create("light.station_sunlight_render", true, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    // Sunlight Changes: Leave the two-frame quality and GPU cost decision with each player.
    public static readonly CVarDef<bool> StationSunlightTemporal =
        CVarDef.Create("light.station_sunlight_temporal", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool> AmbientOcclusion =
        CVarDef.Create("light.ambient_occlusion", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Distance in world-pixels of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<string> AmbientOcclusionColor =
        CVarDef.Create("light.ambient_occlusion_color", "#04080FAA", CVar.CLIENTONLY);

    /// <summary>
    /// Distance in world-pixels of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<float> AmbientOcclusionDistance =
        CVarDef.Create("light.ambient_occlusion_distance", 4f, CVar.CLIENTONLY);
}
