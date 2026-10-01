using Dalamud.Configuration;

namespace RetainerPricer;

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool AutoPriceNewListings { get; set; } = true;
    public bool OpenWithRetainer { get; set; } = true;
    public PriceSource Source { get; set; } = PriceSource.Universalis;
    public int MaximumAgeMinutes { get; set; } = 15;
    public int MinimumPrice { get; set; } = 1;
    public bool OnlyLowerExistingPrices { get; set; } = true;

    public void Normalize()
    {
        if (!Enum.IsDefined(Source)) Source = PriceSource.Universalis;
        MaximumAgeMinutes = Math.Clamp(MaximumAgeMinutes, 1, 120);
        MinimumPrice = Math.Clamp(MinimumPrice, 1, 999_999_999);
    }
}
