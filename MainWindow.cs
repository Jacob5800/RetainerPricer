using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerPricer;

internal sealed class MainWindow : Window
{
    private readonly PluginConfig config;
    private readonly PricingController controller;
    private readonly IReadOnlyList<ItemChoice> itemChoices;
    private readonly Func<MarketWorld?> homeWorld;
    private readonly Action save;
    private readonly Action<Action> dispatch;
    private readonly Func<string?> localError;
    private readonly Func<string?> retainerError;
    private string lookupSearch = "";
    private string lookupSearchCache = "";
    private List<ItemChoice> lookupMatches = [];
    private ItemChoice? lookupSelection;
    private bool lookupHq;
    private string exceptionSearch = "";
    private string exceptionSearchCache = "";
    private List<ItemChoice> exceptionMatches = [];

    public MainWindow(PluginConfig config, PricingController controller, IReadOnlyList<ItemChoice> itemChoices,
        Func<MarketWorld?> homeWorld, Action save, Action<Action> dispatch,
        Func<string?> localError, Func<string?> retainerError) : base("Retainer Pricer")
    {
        (this.config, this.controller, this.itemChoices, this.homeWorld, this.save, this.dispatch, this.localError, this.retainerError) =
            (config, controller, itemChoices, homeWorld, save, dispatch, localError, retainerError);
        Size = new Vector2(860, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("One gil below the lowest matching listing on your home world. HQ and NQ are compared separately; your own retainers are excluded.");
        if (retainerError() is { } nativeError) ImGui.TextWrapped(nativeError);
        var hasRetainer = controller.HasRetainer;
        ImGui.BeginDisabled(controller.Busy || !hasRetainer || !controller.CanStartListingItems);
        if (ImGui.Button("Start listing items")) dispatch(controller.StartListingItems);
        ImGui.SameLine();
        if (ImGui.Button("Update existing listings")) dispatch(controller.UpdateExistingListings);
        ImGui.EndDisabled();
        if (!hasRetainer)
            ImGui.TextDisabled("Open a retainer's selling list on your home world to enable automatic pricing.");
        else
            ImGui.TextDisabled("Start listing items automatically prices and lists eligible carried inventory. Update existing listings reprices current stock. Both use fresh local marketboard data and skip exclusions.");
        if (controller.StartListingAvailabilityError is { } pricingError) ImGui.TextWrapped(pricingError);
        ImGui.BeginDisabled(controller.Busy);
        var automatic = config.AutoPriceNewListings;
        if (ImGui.Checkbox("Automatically price new listings", ref automatic)) { config.AutoPriceNewListings = automatic; save(); }
        ImGui.TextDisabled("For batch actions, use the two buttons above; no price review or manual item entry is needed.");
        var source = (int)config.Source;
        ImGui.SetNextItemWidth(250);
        if (ImGui.Combo("Price source", ref source, "Universalis\0Local marketboard\0"))
        { config.Source = (PriceSource)source; save(); }
        if (config.Source == PriceSource.Universalis)
            ImGui.TextWrapped(config.UseMaximumPriceAge
                ? $"Universalis uses player-uploaded prices and skips data older than {config.MaximumAgeMinutes} minutes. Switch to Local for a fresh game check."
                : "Universalis uses player-uploaded prices. Price age is not filtered; switch to Local for a fresh game check.");
        else
        {
            ImGui.TextWrapped("Local opens Compare Prices and waits for the complete marketboard response. Both automatic buttons always use this live local source.");
            if (localError() is { } error) ImGui.TextWrapped(error);
        }
        ImGui.EndDisabled();
        ImGui.Separator();

        ImGui.TextWrapped(controller.Status);
        if (controller.Busy)
        {
            if (!string.IsNullOrEmpty(controller.Progress)) ImGui.TextUnformatted(controller.Progress);
            if (ImGui.Button("Stop")) dispatch(() => controller.Cancel());
        }
        if (ImGui.BeginTabBar("##pricingTabs"))
        {
            if (ImGui.BeginTabItem("New / selected item")) { DrawCurrent(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Price lookup")) { DrawManualLookup(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Existing listings")) { DrawExisting(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Settings")) { DrawSettings(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private void DrawCurrent()
    {
        var item = controller.CurrentItem;
        if (item is null)
        {
            ImGui.TextWrapped("Open Price lookup to search and retrieve a home-world price from the main menu. For batch listing, open a retainer's selling list and click Start listing items.");
            return;
        }
        ImGui.TextUnformatted($"{item.Name}{(item.IsHq ? " (HQ)" : " (NQ)")} · {item.Quantity:N0} items");
        ImGui.TextUnformatted($"{item.Session.WorldName} · current asking price: {item.CurrentPrice:N0} gil each");
        ImGui.BeginDisabled(controller.Busy);
        if (ImGui.Button("Check price again")) dispatch(() => controller.CheckCurrent());
        ImGui.EndDisabled();
        if (controller.CurrentSnapshot is { } snapshot)
        {
            DrawAge(snapshot);
            if (controller.CurrentProposal is { } quote)
            {
                if (quote.CanApply)
                {
                    ImGui.TextUnformatted($"Lowest matching listing: {quote.LowestPrice:N0} gil each");
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.6f, 1), $"Your price: {quote.SuggestedPrice:N0} gil each");
                    ImGui.TextUnformatted($"Stack before tax: {(ulong)quote.SuggestedPrice * item.Quantity:N0} gil");
                    ImGui.BeginDisabled(controller.Busy);
                    if (ImGui.Button("Apply price to selling window")) dispatch(controller.FillCurrent);
                    ImGui.EndDisabled();
                }
                else ImGui.TextWrapped(quote.Error ?? "No usable price.");
            }
        }
    }

    private void DrawExisting()
    {
        ImGui.TextWrapped("The Update existing listings button checks each eligible listing against the live local marketboard, sets it one gil below the lowest comparable listing, then verifies the retainer accepted the change. Rows without a safe price are left untouched.");
        if (controller.ExistingUpdateError is { } updateError) ImGui.TextWrapped(updateError);
        if (controller.ExistingApplyError is { } applyError) ImGui.TextWrapped(applyError);
        if (controller.Rows.Count == 0) return;
        if (ImGui.BeginTable("##existingPrices", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable, new Vector2(0, Math.Max(150, ImGui.GetContentRegionAvail().Y - 30))))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 45);
            ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 74);
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 74);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Result");
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var row in controller.Rows)
            {
                ImGui.PushID(row.Item.Slot);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextWrapped(row.Item.Name + (row.Item.IsHq ? " (HQ)" : ""));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Item.Quantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Item.CurrentPrice.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Proposal is { CanApply: true } p ? p.SuggestedPrice.ToString("N0") : "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Snapshot?.Source.ToString() ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Snapshot is { } data ? Age(data.ObservedAt) : "—");
                ImGui.TableNextColumn(); ImGui.TextWrapped(row.Status);
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(controller.Busy || config.ExcludedItemIds.Contains(row.Item.ItemId));
                if (ImGui.SmallButton("Exclude")) AddExclusion(row.Item.ItemId);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private void DrawSettings()
    {
        ImGui.BeginDisabled(controller.Busy);
        var minimum = config.MinimumPrice;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Minimum price per item (gil)", ref minimum))
        { config.MinimumPrice = minimum; config.Normalize(); save(); }
        var age = config.MaximumAgeMinutes;
        var filterAge = config.UseMaximumPriceAge;
        if (ImGui.Checkbox("Filter out prices older than", ref filterAge))
        { config.UseMaximumPriceAge = filterAge; save(); }
        if (config.UseMaximumPriceAge)
        {
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("Maximum age (minutes)", ref age))
            { config.MaximumAgeMinutes = age; config.Normalize(); save(); }
        }
        var open = config.OpenWithRetainer;
        if (ImGui.Checkbox("Open this window with the retainer selling list", ref open)) { config.OpenWithRetainer = open; save(); }
        ImGui.EndDisabled();
        ImGui.Separator();
        DrawExceptions();
        ImGui.TextWrapped("Prices are per item, before tax. If an undercut would be below your minimum, or no matching competitor is available, that item is left unchanged.");
        ImGui.TextWrapped("Closing the retainer or changing character/world stops a batch. Stop prevents further submissions; completed price changes stay applied.");
    }

    private void DrawManualLookup()
    {
        ImGui.TextWrapped("Search any item and retrieve its home-world price from Universalis without opening a retainer sale window. This lookup is read-only; it never changes a listing.");
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("Search item", ref lookupSearch, 128);
        if (!StringComparer.CurrentCultureIgnoreCase.Equals(lookupSearch, lookupSearchCache))
            lookupSelection = null;
        RefreshMatches(lookupSearch, ref lookupSearchCache, ref lookupMatches);
        if (!string.IsNullOrWhiteSpace(lookupSearch) && lookupMatches.Count > 0)
        {
            ImGui.BeginChild("##lookupMatches", new Vector2(0, Math.Min(160, lookupMatches.Count * 22 + 8)), true);
            foreach (var candidate in lookupMatches)
            {
                ImGui.PushID((int)candidate.ItemId);
                if (ImGui.Selectable($"{candidate.Name}  ·  #{candidate.ItemId}", lookupSelection?.ItemId == candidate.ItemId))
                {
                    lookupSelection = candidate;
                    lookupSearch = candidate.Name;
                    lookupSearchCache = lookupSearch;
                    lookupMatches = [];
                }
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        else if (lookupSearch.Trim().Length >= 2)
            ImGui.TextDisabled("No item names match that search.");

        if (lookupSelection is { } selected)
        {
            ImGui.TextUnformatted($"Selected: {selected.Name} · item {selected.ItemId}");
            var hq = lookupHq;
            ImGui.BeginDisabled(controller.Busy);
            if (ImGui.Checkbox("High Quality", ref hq)) lookupHq = hq;
            ImGui.EndDisabled();
            var world = homeWorld();
            ImGui.TextUnformatted(world is null ? "Waiting for character home-world data." : $"World: {world.Name}");
            ImGui.BeginDisabled(controller.Busy || world is null);
            if (ImGui.Button("Retrieve price") && world is not null)
                dispatch(() => controller.CheckManualItem(selected, lookupHq, world));
            ImGui.EndDisabled();
            if (controller.ManualQuoteWarning is { } warning) ImGui.TextWrapped(warning);
            if (controller.ManualSnapshot is { } snapshot)
            {
                DrawAge(snapshot);
                var comparable = snapshot.Listings.Where(listing => listing.IsHq == lookupHq && !listing.OnMannequin).ToArray();
                if (comparable.Length > 0)
                    ImGui.TextUnformatted($"Lowest retrieved matching listing: {comparable.Min(listing => listing.PricePerUnit):N0} gil each");
                else
                    ImGui.TextUnformatted("No matching HQ/NQ listings were included in this response.");
                ImGui.TextUnformatted($"Received {snapshot.Listings.Count:N0} listings on {world?.Name ?? "the home world"}.");
                if (controller.ManualProposal is { } proposal)
                {
                    if (proposal.CanApply)
                    {
                        if (controller.ManualQuoteWarning is null)
                            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.6f, 1), $"Suggested undercut: {proposal.SuggestedPrice:N0} gil each");
                        else
                            ImGui.TextUnformatted($"Reference undercut: {proposal.SuggestedPrice:N0} gil each");
                    }
                    else
                        ImGui.TextWrapped(proposal.Error ?? "No usable price.");
                }
            }
        }
        DrawCapturedItems();
    }

    private void DrawCapturedItems()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Captured items");
        ImGui.TextWrapped("Open a retainer's selling list and use Start listing items above. It snapshots carried inventory, skips untradeable, nonmarketable, and excluded items, checks live local prices, and confirms each eligible sale one gil below the lowest matching listing. It stops when inventory is done or all 20 retainer slots are full. Use Stop to halt the batch.");
        ImGui.BeginDisabled(controller.Busy);
        if (ImGui.Button("Snapshot inventory")) dispatch(controller.SnapshotInventory);
        ImGui.SameLine();
        if (ImGui.Button("Snapshot retainer listings")) dispatch(controller.SnapshotListedItems);
        ImGui.EndDisabled();
        if (controller.StartListingAvailabilityError is { } listingAvailabilityError)
            ImGui.TextWrapped(listingAvailabilityError);

        if (controller.InventorySnapshotError is { } inventoryError) ImGui.TextWrapped(inventoryError);
        else if (controller.InventorySnapshotAt is { } inventoryAt)
        {
            ImGui.TextUnformatted($"Inventory snapshot · {controller.InventoryCandidates.Count} stack(s) · {inventoryAt:HH:mm:ss}");
            ImGui.TextDisabled($"Skipped {controller.InventoryExceptionSkipped} excluded stack(s) and {controller.InventoryUnmarketableSkipped} untradeable or nonmarketable stack(s).");
            DrawInventoryCandidates();
        }

        if (controller.ListedSnapshotError is { } listedError) ImGui.TextWrapped(listedError);
        else if (controller.ListedSnapshotAt is { } listedAt)
        {
            ImGui.TextUnformatted($"Retainer listing snapshot · {controller.ListedCandidates.Count} item(s) · {listedAt:HH:mm:ss}");
            ImGui.TextDisabled($"Skipped {controller.ListedExceptionSkipped} excluded listing(s) and {controller.ListedUnmarketableSkipped} unmarketable listing(s).");
            DrawListedCandidates();
        }
    }

    private void DrawInventoryCandidates()
    {
        if (controller.InventoryCandidates.Count == 0) { ImGui.TextDisabled("No eligible inventory stacks to list or price."); return; }
        if (!ImGui.BeginTable("##inventorySnapshot", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, 190))) return;
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        var world = homeWorld();
        foreach (var item in controller.InventoryCandidates.ToArray())
        {
            ImGui.PushID(HashCode.Combine(item.ItemId, item.InventoryType, item.Slot));
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextWrapped($"{item.Name}{(item.IsHq ? " (HQ)" : " (NQ)")}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(controller.Busy || world is null);
            if (ImGui.SmallButton("Retrieve")) dispatch(() => controller.CheckManualItem(new ItemChoice(item.ItemId, item.Name), item.IsHq, world!));
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(controller.Busy);
            if (ImGui.SmallButton("List")) dispatch(() => controller.OpenInventoryItem(item));
            ImGui.SameLine();
            if (ImGui.SmallButton("Exclude")) AddExclusion(item.ItemId);
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawListedCandidates()
    {
        if (controller.ListedCandidates.Count == 0) { ImGui.TextDisabled("No eligible listings on this retainer."); return; }
        if (!ImGui.BeginTable("##retainerSnapshot", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, 160))) return;
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        var world = homeWorld();
        foreach (var item in controller.ListedCandidates.ToArray())
        {
            ImGui.PushID(HashCode.Combine(item.ItemId, item.Slot));
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextWrapped($"{item.Name}{(item.IsHq ? " (HQ)" : " (NQ)")}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Quantity.ToString("N0"));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(item.CurrentPrice.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(controller.Busy || world is null);
            if (ImGui.SmallButton("Retrieve")) dispatch(() => controller.CheckManualItem(new ItemChoice(item.ItemId, item.Name), item.IsHq, world!));
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(controller.Busy);
            if (ImGui.SmallButton("Exclude")) AddExclusion(item.ItemId);
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void AddExclusion(uint itemId)
    {
        if (!config.ExcludedItemIds.Contains(itemId)) config.ExcludedItemIds.Add(itemId);
        config.Normalize();
        controller.ExcludeItem(itemId);
        save();
    }

    private void DrawExceptions()
    {
        ImGui.TextUnformatted("Item exceptions");
        ImGui.TextWrapped("Excluded items are skipped during automatic new-listing pricing and existing-listing scans. Manual price lookups remain available.");
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("Find an item to exclude", ref exceptionSearch, 128);
        RefreshMatches(exceptionSearch, ref exceptionSearchCache, ref exceptionMatches);
        if (!string.IsNullOrWhiteSpace(exceptionSearch))
        {
            foreach (var candidate in exceptionMatches)
            {
                if (config.ExcludedItemIds.Contains(candidate.ItemId)) continue;
                ImGui.PushID((int)candidate.ItemId);
                ImGui.TextUnformatted($"{candidate.Name}  ·  #{candidate.ItemId}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Add exception"))
                {
                    config.ExcludedItemIds.Add(candidate.ItemId);
                    config.Normalize();
                    save();
                }
                ImGui.PopID();
            }
        }
        if (config.ExcludedItemIds.Count == 0) { ImGui.TextDisabled("No item exceptions."); return; }
        ImGui.TextUnformatted("Excluded:");
        foreach (var itemId in config.ExcludedItemIds.ToArray())
        {
            var name = itemChoices.FirstOrDefault(x => x.ItemId == itemId)?.Name ?? $"Item {itemId}";
            ImGui.PushID((int)itemId);
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                config.ExcludedItemIds.Remove(itemId);
                save();
            }
            ImGui.PopID();
        }
    }

    private void RefreshMatches(string query, ref string previousQuery, ref List<ItemChoice> results)
    {
        if (StringComparer.CurrentCultureIgnoreCase.Equals(query, previousQuery)) return;
        previousQuery = query;
        results = query.Trim().Length < 2 ? [] : itemChoices
            .Where(item => item.Name.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Take(12)
            .ToList();
    }

    private static void DrawAge(PriceSnapshot snapshot)
        => ImGui.TextUnformatted($"{snapshot.Source}: {Age(snapshot.ObservedAt)} old · {snapshot.ObservedAt.ToLocalTime():HH:mm:ss}");

    private static string Age(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        return age.TotalMinutes >= 1 ? $"{Math.Max(0, (int)age.TotalMinutes)}m" : $"{Math.Max(0, (int)age.TotalSeconds)}s";
    }
}
