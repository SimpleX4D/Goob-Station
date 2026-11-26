// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared.Light.Components;

// Sunlight Changes: Store the star on the map entity so joining clients inherit its settings with normal map state.
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MapSunlightComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    // Sunlight Changes: Specify a bearing towards the star; its shadows project in the opposite direction.
    [DataField, AutoNetworkedField]
    public Angle StarBearing = Angle.FromDegrees(45);

    [DataField, AutoNetworkedField]
    public Color Color = Color.FromHex("#FFDC82").WithAlpha(0.8f);
}
