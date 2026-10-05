namespace RetainerPricer;

internal static class ItemProtection
{
    public static bool TryGetExcludedItemIds(NativeMarketBridge bridge, PluginConfig config,
        out HashSet<uint> excludedItemIds, out IReadOnlySet<uint> savedGearsetItemIds, out string error)
    {
        excludedItemIds = config.ExcludedItemIds.ToHashSet();
        savedGearsetItemIds = new HashSet<uint>();
        if (!bridge.TryReadSavedGearsetItemIds(out savedGearsetItemIds, out var gearsetError))
        {
            error = $"{gearsetError} Automatic listing, repricing, and Auto vendor are paused for safety.";
            return false;
        }

        excludedItemIds.UnionWith(savedGearsetItemIds);
        error = string.Empty;
        return true;
    }

    public static string Reason(uint itemId, PluginConfig config, IReadOnlySet<uint> savedGearsetItemIds) =>
        savedGearsetItemIds.Contains(itemId)
            ? "Protected because this item is assigned to a saved gear set."
            : config.ExcludedItemIds.Contains(itemId)
                ? "Skipped because this item is in Exceptions."
                : "Skipped because this item is excluded.";
}
