using Dalamud.Configuration;

namespace RetainerPricer;

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 4;
    public bool AutoPriceNewListings { get; set; } = true;
    public bool OpenWithRetainer { get; set; } = true;
    public PriceSource Source { get; set; } = PriceSource.Universalis;
    public int MaximumAgeMinutes { get; set; } = 15;
    public bool UseMaximumPriceAge { get; set; }
    public int UniversalisCacheMinutes { get; set; } = 5;
    public bool UseDataCenterPrices { get; set; }
    public bool UseRegionPrices { get; set; }
    public int MinimumPrice { get; set; } = 1;
    public List<uint> ExcludedItemIds { get; set; } = [];
    public Dictionary<uint, uint> BatchSaleQuantities { get; set; } = [];
    // Zero or missing means unlimited total quantity for that item during one listing run.
    public Dictionary<uint, uint> BatchSaleMaxQuantities { get; set; } = [];

    public void Normalize()
    {
        if (!Enum.IsDefined(Source)) Source = PriceSource.Universalis;
        MaximumAgeMinutes = Math.Clamp(MaximumAgeMinutes, 1, 120);
        UniversalisCacheMinutes = Math.Clamp(UniversalisCacheMinutes, 0, 60);
        if (UseRegionPrices) UseDataCenterPrices = false;
        MinimumPrice = Math.Clamp(MinimumPrice, 1, 999_999_999);
        ExcludedItemIds = (ExcludedItemIds ?? []).Where(id => id != 0).Distinct().ToList();
        BatchSaleQuantities = (BatchSaleQuantities ?? []).Where(pair => pair.Key != 0)
            .ToDictionary(pair => pair.Key, pair => (uint)Math.Clamp((long)pair.Value, 1, 9_999));
        BatchSaleMaxQuantities = (BatchSaleMaxQuantities ?? []).Where(pair => pair.Key != 0 && BatchSaleQuantities.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value == 0 ? 0 : (uint)Math.Clamp((long)pair.Value, 1, 999_999_999));
    }
}
