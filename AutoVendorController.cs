namespace RetainerPricer;

internal sealed class AutoVendorController : IDisposable
{
    private enum Step { Idle, Begin, Price, ContextMenu, SalePrompt, Confirm, Verify }

    private readonly NativeMarketBridge bridge;
    private readonly UniversalisClient universalis;
    private readonly PluginConfig config;
    private readonly IReadOnlySet<uint> marketableItemIds;
    private CancellationTokenSource cancellation = new();
    private Task<PriceSnapshot>? priceTask;
    private IReadOnlyList<CarriedItemCandidate> candidates = [];
    private IReadOnlySet<ulong> ownRetainerIds = new HashSet<ulong>();
    private MarketWorld? world;
    private CarriedItemCandidate? candidate;
    private Step step;
    private int index;
    private uint quantityBefore;
    private uint targetReduction;
    private int priceThreshold;
    private int cacheMinutes;
    private string? dataCenterName;
    private bool useRegionPrices;
    private DateTimeOffset deadline;

    public AutoVendorController(NativeMarketBridge bridge, UniversalisClient universalis,
        PluginConfig config, IReadOnlySet<uint> marketableItemIds)
    {
        this.bridge = bridge;
        this.universalis = universalis;
        this.config = config;
        this.marketableItemIds = marketableItemIds;
    }

    public bool IsRunning => step != Step.Idle;
    public string Status { get; private set; } = "Auto vendor is stopped.";
    public int Progress => candidates.Count == 0 ? 0 : Math.Min(index + 1, candidates.Count);
    public int CandidateCount => candidates.Count;

    public void Start()
    {
        if (IsRunning) return;
        world = bridge.GetHomeWorld();
        if (world is null)
        { Status = "Log in to start Auto vendor."; return; }
        if (!bridge.IsVendorShopOpen)
        { Status = "Open an NPC vendor's Shop window before starting Auto vendor."; return; }
        if (!bridge.TryGetOwnRetainerIds(out ownRetainerIds))
        { Status = "Your retainer ownership data is not ready. Reopen the vendor window and retry; your own listings must be excluded."; return; }

        candidates = bridge.ReadCarriedInventory(marketableItemIds, config.ExcludedItemIds.ToHashSet(),
            out var excluded, out var unmarketable, out var inventoryError);
        if (inventoryError.Length != 0)
        { Status = inventoryError; candidates = []; return; }
        if (candidates.Count == 0)
        { Status = $"No eligible carried items to check. {excluded} excluded and {unmarketable} unmarketable stack(s) were skipped."; return; }

        priceThreshold = config.AutoVendorPriceThreshold;
        cacheMinutes = config.UniversalisCacheMinutes;
        dataCenterName = config.UseDataCenterPrices || config.UseRegionPrices ? world.DataCenterName : null;
        useRegionPrices = config.UseRegionPrices;
        index = 0;
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
        step = Step.Begin;
        Status = $"Checking {candidates.Count} carried stack(s) against Universalis. Items priced at or below {priceThreshold:N0} gil will be sold to the open vendor; excluded items are skipped.";
    }

    public void Update()
    {
        if (!IsRunning) return;
        try
        {
            switch (step)
            {
                case Step.Begin:
                    BeginCandidate();
                    break;
                case Step.Price:
                    CheckPrice();
                    break;
                case Step.ContextMenu:
                    WaitForContextMenu();
                    break;
                case Step.SalePrompt:
                    WaitForSalePrompt();
                    break;
                case Step.Confirm:
                    WaitForConfirmation();
                    break;
                case Step.Verify:
                    VerifySale();
                    break;
            }
        }
        catch (Exception ex)
        {
            Cancel($"Auto vendor stopped after an unexpected error: {ex.Message}");
        }
    }

    private void BeginCandidate()
    {
        if (index >= candidates.Count)
        {
            Finish($"Auto vendor complete. Checked {candidates.Count} carried stack(s). {priceThreshold:N0}-gil threshold; all qualifying items were sold or skipped safely.");
            return;
        }
        if (world is null || bridge.GetHomeWorld()?.WorldId != world.WorldId)
        { Cancel("Auto vendor stopped because your home world changed."); return; }
        if (!bridge.IsVendorShopOpen)
        { Cancel("Auto vendor stopped because the vendor Shop window closed."); return; }

        var planned = candidates[index];
        if (!marketableItemIds.Contains(planned.ItemId) || config.ExcludedItemIds.Contains(planned.ItemId))
        { Skip($"Skipped {planned.Name}: it is excluded or no longer marketable."); return; }

        var inventory = bridge.ReadCarriedInventory(marketableItemIds, config.ExcludedItemIds.ToHashSet(),
            out _, out _, out var inventoryError);
        if (inventoryError.Length != 0)
        { Cancel($"Auto vendor stopped because inventory could not be refreshed: {inventoryError}"); return; }
        candidate = inventory.FirstOrDefault(item => item.ItemId == planned.ItemId && item.IsHq == planned.IsHq);
        if (candidate is null)
        { Skip($"Skipped {planned.Name}: no matching carried stack remains."); return; }

        priceTask = universalis.FetchAsync(world.WorldId, candidate.ItemId, cancellation.Token,
            cacheMinutes, dataCenterName, useRegionPrices);
        deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        step = Step.Price;
        Status = $"Checking the Universalis price for {candidate.Name}{(candidate.IsHq ? " (HQ)" : " (NQ)")} · {index + 1} of {candidates.Count}…";
    }

    private void CheckPrice()
    {
        if (candidate is not { } item || world is null || priceTask is null)
        { Cancel("Auto vendor stopped because the active item price check was lost."); return; }
        if (!priceTask.IsCompleted)
        {
            if (DateTimeOffset.UtcNow > deadline) Skip($"Skipped {item.Name}: Universalis did not return a price in time.");
            return;
        }

        PriceSnapshot snapshot;
        try { snapshot = priceTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception ex) { Skip($"Skipped {item.Name}: price lookup failed ({ex.Message})."); return; }
        finally { priceTask = null; }

        if (!snapshot.IsComplete)
        { Skip($"Skipped {item.Name}: Universalis did not return a complete market listing set."); return; }
        var lowest = snapshot.Listings
            .Where(listing => listing.IsHq == item.IsHq && listing.PricePerUnit > 0 &&
                              !ownRetainerIds.Contains(listing.RetainerId))
            .Select(listing => (uint?)listing.PricePerUnit)
            .Min();
        if (lowest is null)
        { Skip($"Skipped {item.Name}: no competing {(item.IsHq ? "HQ" : "NQ")} market listing was found."); return; }
        if (lowest.Value > priceThreshold)
        { Skip($"Kept {item.Name}: lowest matching market listing is {lowest.Value:N0} gil, above the {priceThreshold:N0}-gil threshold."); return; }
        if (!bridge.TryGetCarriedItemTotal(item.ItemId, item.IsHq, out quantityBefore, out var countError))
        { Cancel($"Auto vendor stopped before selling {item.Name}: {countError}"); return; }
        targetReduction = item.Quantity;
        if (quantityBefore < targetReduction)
        { Cancel($"Auto vendor stopped because the carried quantity for {item.Name} changed unexpectedly."); return; }
        if (!bridge.TryOpenVendorSellContext(item, out var openError))
        { Cancel($"Auto vendor stopped before selling {item.Name}: {openError}"); return; }
        deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        step = Step.ContextMenu;
        Status = $"{item.Name} is listed at {lowest.Value:N0} gil · opening its vendor Sell action…";
    }

    private void WaitForContextMenu()
    {
        if (candidate is not { } item) { Cancel("Auto vendor stopped because the active item was lost."); return; }
        if (bridge.IsVendorContextMenuOpen)
        {
            if (!bridge.TrySelectVendorSell(item, out var selectError))
            { Cancel($"Auto vendor stopped before selling {item.Name}: {selectError}"); return; }
            deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            step = Step.SalePrompt;
            Status = $"Selected Sell for {item.Name}; waiting for the game's quantity or confirmation prompt…";
            return;
        }
        if (DateTimeOffset.UtcNow > deadline)
            Cancel($"Auto vendor stopped because the vendor Sell menu did not open for {item.Name}.");
    }

    private void WaitForSalePrompt()
    {
        if (candidate is not { } item) { Cancel("Auto vendor stopped because the active item was lost."); return; }
        if (bridge.IsVendorQuantityPromptOpen)
        {
            if (!bridge.TrySetVendorSaleQuantity(item, out var quantityError))
            { Cancel($"Auto vendor left {item.Name} unsold: {quantityError}"); return; }
            deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            step = Step.Confirm;
            Status = $"Set the vendor quantity to the full {item.Quantity:N0}-item stack of {item.Name}; waiting for confirmation…";
            return;
        }
        if (bridge.IsVendorConfirmationOpen)
        {
            ConfirmSale(item);
            return;
        }
        if (!bridge.TryGetCarriedItemTotal(item.ItemId, item.IsHq, out var current, out var inventoryError))
        {
            if (DateTimeOffset.UtcNow > deadline) Cancel($"Auto vendor could not verify whether {item.Name} was sold: {inventoryError}");
            return;
        }
        if (current < quantityBefore)
        {
            step = Step.Verify;
            deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            return;
        }
        if (DateTimeOffset.UtcNow > deadline)
            Cancel($"Auto vendor stopped because no sale prompt appeared for {item.Name}. No next item was attempted.");
    }

    private void WaitForConfirmation()
    {
        if (candidate is not { } item) { Cancel("Auto vendor stopped because the active item was lost."); return; }
        if (bridge.IsVendorConfirmationOpen)
        {
            ConfirmSale(item);
            return;
        }
        if (bridge.TryGetCarriedItemTotal(item.ItemId, item.IsHq, out var current, out _) && current < quantityBefore)
        {
            step = Step.Verify;
            deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            return;
        }
        if (DateTimeOffset.UtcNow > deadline)
            Cancel($"Auto vendor stopped because the sale confirmation for {item.Name} did not appear. No next item was attempted.");
    }

    private void ConfirmSale(CarriedItemCandidate item)
    {
        if (!bridge.TryConfirmVendorSale(item, out var confirmError))
        { Cancel($"Auto vendor left {item.Name} unconfirmed: {confirmError}"); return; }
        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        step = Step.Verify;
        Status = $"Confirmed the vendor sale for {item.Name}; verifying inventory before continuing…";
    }

    private void VerifySale()
    {
        if (candidate is not { } item) { Cancel("Auto vendor stopped because the active item was lost."); return; }
        if (!bridge.TryGetCarriedItemTotal(item.ItemId, item.IsHq, out var current, out var error))
        {
            if (DateTimeOffset.UtcNow > deadline) Cancel($"The vendor sale for {item.Name} could not be verified: {error}");
            return;
        }
        var expectedRemaining = quantityBefore - targetReduction;
        if (current == expectedRemaining)
        {
            index++;
            candidate = null;
            step = Step.Begin;
            Status = $"Sold {item.Name} × {targetReduction:N0} to the vendor. Continuing to the next eligible stack…";
            return;
        }
        if (current < expectedRemaining)
        { Cancel($"Inventory changed by more than the expected {targetReduction:N0} {item.Name}; the vendor run stopped to avoid another sale."); return; }
        if (DateTimeOffset.UtcNow > deadline)
            Cancel($"The vendor sale for {item.Name} could not be verified. Check the inventory before starting another run.");
    }

    private void Skip(string message)
    {
        index++;
        candidate = null;
        priceTask = null;
        step = Step.Begin;
        Status = message;
    }

    private void Finish(string message)
    {
        step = Step.Idle;
        Status = message;
        candidates = [];
        candidate = null;
        priceTask = null;
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
    }

    public void Cancel(string message = "Auto vendor stopped. Check any open vendor prompt before continuing.")
    {
        if (candidate is { } item) bridge.TryCancelVendorPrompt(item);
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        step = Step.Idle;
        Status = message;
        candidates = [];
        candidate = null;
        priceTask = null;
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
    }

    public void Dispose()
    {
        Cancel();
        cancellation.Dispose();
    }
}
