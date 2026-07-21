using System.Collections.Generic;

namespace DynamicTextureManager.UI;

public enum ColorId
{
    HeaderButtons,
    DisabledMod,
}

public static class Colors
{
    public const uint SelectedRed = 0xFF2020D0;

    public static (uint DefaultColor, string Name, string Description) Data(this ColorId color)
        => color switch
        {
            // @formatter:off
            ColorId.HeaderButtons              => (0xFFFFF0C0, "Header Buttons", "The text and border color of buttons in the header."),
            ColorId.DisabledMod                => (0xFF808080, "Disabled Mod", "The text color of dTextures whose generated mod is currently disabled in Penumbra."),
            _                                  => (0x00000000, string.Empty, string.Empty),
            // @formatter:on
        };

    private static IReadOnlyDictionary<ColorId, uint> _colors = new Dictionary<ColorId, uint>();

    /// <summary> Obtain the configured value for a color. </summary>
    public static uint Value(this ColorId color)
        => _colors.TryGetValue(color, out var value) ? value : color.Data().DefaultColor;

    /// <summary> Set the configurable colors dictionary to a value. </summary>
    public static void SetColors(Configuration config)
        => _colors = config.Colors;
}