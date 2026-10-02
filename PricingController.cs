namespace RetainerPricer;

internal sealed class PriceRow(SellItem item)
{
    public SellItem Item { get; } = item;
    public PriceSnapshot? Snapshot { get; set; }
    public PriceProposal? Proposal { get; set; }
    public string Status { get; set; } = "Waiting";
}

internal sealed class PricingController : IDisposable
{
    private enum Work { Idle, Single, Manual, Scan, BatchListing }
    private enum Step { Start, Opening, WaitingToCompare, Quote, Closing, ClosingListingCompare, ClosingSkippedCompare, ClosingSkippedSell, Confirming }
    private readonly NativeMarketBridge bridge;
    private readonly UniversalisClient universalis;
    private readonly PluginConfig config;
    private readonly IReadOnlySet<uint> marketableItemIds;
    private CancellationTokenSource cancellation = new();
    private readonly Dictionary<(uint World, uint Item), PriceSnapshot> cache = [];
    private Task<PriceSnapshot>? quoteTask;
    private Work work;
    private Step step;
    private int index;
    private long seenDialog = -1;
    private DateTimeOffset nextTick;
    private DateTimeOffset deadline;
    private string? pendingSkipMessage;
    private SellItem? workingItem;
    private ManualQuoteTarget? manualTarget;
    private MarketSession? session;
    private bool autoApply;
    private bool localRequested;
    private PriceSource workSource;
    private uint submittedPrice;
    private List<CarriedItemCandidate> batchCandidates = [];
    private HashSet<int> preListingSlots = [];
    private CarriedItemCandidate? listingCandidate;
    private uint listingSubmittedPrice;
    private int listingSucceeded;
    private int listingSkipped;

    private sealed record ManualQuoteTarget(ItemChoice Item, bool IsHq, MarketWorld World);

    public PricingController(NativeMarketBridge bridge, UniversalisClient universalis, PluginConfig config,
        IReadOnlySet<uint> marketableItemIds)
        => (this.bridge, this.universalis, this.config, this.marketableItemIds) =
            (bridge, universalis, config, marketableItemIds);

    public SellItem? CurrentItem { get; private set; }
    public PriceSnapshot? CurrentSnapshot { get; private set; }
    public PriceProposal? CurrentProposal { get; private set; }
    public PriceSnapshot? ManualSnapshot { get; private set; }
    public PriceProposal? ManualProposal { get; private set; }
    public string? ManualQuoteWarning { get; private set; }
    public List<CarriedItemCandidate> InventoryCandidates { get; } = [];
    public List<CarriedItemCandidate> ExceptionInventoryCandidates { get; } = [];
    public List<SellItem> ListedCandidates { get; } = [];
    public DateTimeOffset? InventorySnapshotAt { get; private set; }
    public DateTimeOffset? ExceptionInventorySnapshotAt { get; private set; }
    public DateTimeOffset? ListedSnapshotAt { get; private set; }
    public int InventoryExceptionSkipped { get; private set; }
    public int InventoryUnmarketableSkipped { get; private set; }
    public int ExceptionInventoryUnmarketableSkipped { get; private set; }
    public int ListedExceptionSkipped { get; private set; }
    public int ListedUnmarketableSkipped { get; private set; }
    public string? InventorySnapshotError { get; private set; }
    public string? ExceptionInventorySnapshotError { get; private set; }
    public string? ListedSnapshotError { get; private set; }
    public List<PriceRow> Rows { get; } = [];
    public bool Busy => work != Work.Idle;
    public bool IsListingItemsRunning => work == Work.BatchListing;
    public bool IsUpdatingListings => work == Work.Scan;
    public bool CanUpdateExisting => bridge.RetainerAvailabilityError is null;
    public bool CanApplyExisting => CanUpdateExisting && bridge.ItemSelectorAvailabilityError is null;
    public bool CanStartListingItems => CanApplyExisting && bridge.LocalAvailabilityError is null;
    public string? ExistingUpdateError => bridge.RetainerAvailabilityError;
    public string? ExistingApplyError => bridge.ItemSelectorAvailabilityError;
    public string? StartListingAvailabilityError => ExistingUpdateError ?? ExistingApplyError ?? bridge.LocalAvailabilityError;
    public bool HasRetainer => bridge.TryGetSession(out _, out _);
    public string Status { get; private set; } = "Open a retainer's selling list to begin.";
    public string Progress => work is Work.Scan ? $"Checking {Math.Min(index + 1, Rows.Count)} of {Rows.Count}"
        : work is Work.BatchListing ? $"Listing {Math.Min(index + 1, batchCandidates.Count)} of {batchCandidates.Count}"
        : "";

    public void Update()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddMilliseconds(250);
        CurrentItem = bridge.TryReadSellItem(out var selected, out _) ? selected : null;
        if (work == Work.Manual) { TickManual(now); return; }
        if (work != Work.Idle && (session is null || !bridge.TryGetSession(out var active, out _) || active != session))
        {
            Cancel("Stopped because the character, world or retainer changed.");
            return;
        }
        if (work == Work.Idle)
        {
            if (CurrentItem is { } item && item.DialogGeneration != seenDialog)
            {
                seenDialog = item.DialogGeneration;
                CurrentSnapshot = null;
                CurrentProposal = null;
                if (config.AutoPriceNewListings && !item.IsExisting && marketableItemIds.Contains(item.ItemId) && !IsExcluded(item.ItemId))
                    CheckCurrent(true);
            }
            return;
        }
        if (work == Work.Single) TickSingle(now);
        else if (work == Work.Scan) TickScan(now);
        else if (work == Work.BatchListing) TickBatchListing(now);
    }

    public void CheckManualItem(ItemChoice item, bool isHq, MarketWorld world)
    {
        if (Busy) return;
        if (item.ItemId == 0 || world.WorldId == 0) { Status = "Choose a valid item and wait for your home-world data."; return; }
        ResetRequest();
        manualTarget = new ManualQuoteTarget(item, isHq, world);
        ManualSnapshot = null;
        ManualProposal = null;
        ManualQuoteWarning = null;
        step = Step.Start;
        work = Work.Manual;
        session = null;
        Status = $"Retrieving {item.Name}{(isHq ? " (HQ)" : " (NQ)")} on {world.Name} from Universalis...";
    }

    public void SnapshotInventory()
    {
        var items = bridge.ReadCarriedInventory(marketableItemIds, config.ExcludedItemIds.ToHashSet(),
            out var exceptionSkipped, out var unmarketableSkipped, out var error);
        InventoryCandidates.Clear();
        InventoryCandidates.AddRange(items);
        InventoryExceptionSkipped = exceptionSkipped;
        InventoryUnmarketableSkipped = unmarketableSkipped;
        InventorySnapshotError = error.Length == 0 ? null : error;
        InventorySnapshotAt = error.Length == 0 ? DateTimeOffset.Now : null;
        Status = error.Length != 0 ? error :
            $"Inventory snapshot ready: {items.Count} marketable stack(s), {exceptionSkipped} excluded, {unmarketableSkipped} not marketable.";
    }

    public void SnapshotListedItems()
    {
        if (!bridge.TryGetSession(out _, out var sessionError))
        {
            ListedCandidates.Clear();
            ListedSnapshotAt = null;
            ListedSnapshotError = sessionError;
            Status = sessionError;
            return;
        }
        var items = bridge.ReadExistingListings(out var error);
        ListedCandidates.Clear();
        ListedExceptionSkipped = items.Count(item => config.ExcludedItemIds.Contains(item.ItemId));
        ListedUnmarketableSkipped = items.Count(item => !marketableItemIds.Contains(item.ItemId));
        if (error.Length == 0)
            ListedCandidates.AddRange(items.Where(item => marketableItemIds.Contains(item.ItemId) && !IsExcluded(item.ItemId)));
        ListedSnapshotError = error.Length == 0 ? null : error;
        ListedSnapshotAt = error.Length == 0 ? DateTimeOffset.Now : null;
        Status = error.Length != 0 ? error :
            $"Retainer listing snapshot ready: {ListedCandidates.Count} marketable item(s), {ListedExceptionSkipped} excluded, {ListedUnmarketableSkipped} not marketable.";
    }

    public void StartListingItems()
    {
        if (Busy) return;
        if (!CanStartListingItems)
        {
            Status = StartListingAvailabilityError
                ?? "Automatic listing is unavailable on this client build.";
            return;
        }
        if (!bridge.TryGetSession(out var active, out var sessionError)) { Status = sessionError; return; }
        if (!bridge.TryGetOwnRetainerIds(out _))
        { Status = "Retainer ownership data is not ready. Reopen the retainer selling list and try again; prices will not be submitted until your own listings can be excluded."; return; }
        if (bridge.TryReadSellItem(out _, out _))
        { Status = "Close the individual selling window first, leaving the retainer's selling list open."; return; }
        SnapshotInventory();
        if (InventorySnapshotError is { } snapshotError) { Status = snapshotError; return; }
        if (InventoryCandidates.Count == 0) { Status = "No eligible items in carried inventory. Excluded and unmarketable items are skipped."; return; }
        session = active;
        batchCandidates = InventoryCandidates.ToList();
        index = 0;
        listingSucceeded = 0;
        listingSkipped = 0;
        listingCandidate = null;
        step = Step.Start;
        workSource = PriceSource.Local;
        work = Work.BatchListing;
        Status = $"Automatically listing {batchCandidates.Count} eligible stack(s) using the local marketboard. Each price is checked before the game's Confirm action.";
    }

    public void SnapshotExceptionInventory()
    {
        if (Busy) return;
        // Include already-excluded items so the user can find, inspect, and remove them in this tab.
        var items = bridge.ReadCarriedInventory(marketableItemIds, new HashSet<uint>(),
            out _, out var unmarketableSkipped, out var error);
        ExceptionInventoryCandidates.Clear();
        ExceptionInventoryCandidates.AddRange(items);
        ExceptionInventoryUnmarketableSkipped = unmarketableSkipped;
        ExceptionInventorySnapshotError = error.Length == 0 ? null : error;
        ExceptionInventorySnapshotAt = error.Length == 0 ? DateTimeOffset.Now : null;
        Status = error.Length != 0 ? error :
            $"Exception inventory ready: {items.Count} marketable stack(s), {unmarketableSkipped} untradeable or nonmarketable stack(s) omitted.";
    }

    public bool OpenInventoryItem(CarriedItemCandidate item)
    {
        if (Busy) return false;
        if (!bridge.TryOpenInventoryItem(item, out var error)) { Status = error; return false; }
        Status = $"Opened {item.Name}{(item.IsHq ? " (HQ)" : " (NQ)")} for listing. Confirm the sale in game.";
        return true;
    }

    public void ExcludeItem(uint itemId)
    {
        InventoryCandidates.RemoveAll(item => item.ItemId == itemId);
        ListedCandidates.RemoveAll(item => item.ItemId == itemId);
        foreach (var row in Rows.Where(row => row.Item.ItemId == itemId))
            row.Status = "Excluded";
    }

    public void CheckCurrent(bool fillAutomatically = false)
    {
        if (Busy) return;
        if (!bridge.TryReadSellItem(out var target, out var error)) { Status = error; return; }
        if (!marketableItemIds.Contains(target.ItemId))
        { Status = "This item is not marketable and will be skipped."; return; }
        if (IsExcluded(target.ItemId)) { Status = $"{target.Name} is in the item exception list and will be skipped."; return; }
        workingItem = target;
        session = target.Session;
        CurrentItem = target;
        CurrentSnapshot = null;
        CurrentProposal = null;
        autoApply = fillAutomatically;
        workSource = config.Source;
        ResetRequest();
        work = Work.Single;
        step = Step.Start;
        Status = $"Checking {target.Name} on {target.Session.WorldName}...";
    }

    public void FillCurrent()
    {
        if (Busy || workingItem is null || CurrentSnapshot is null) return;
        CurrentProposal = Calculate(CurrentSnapshot, workingItem);
        if (!CurrentProposal.CanApply) { Status = CurrentProposal.Error!; return; }
        Status = bridge.TryFillPrice(workingItem, CurrentProposal.SuggestedPrice, out var error)
            ? $"Filled {CurrentProposal.SuggestedPrice:N0} gil each. Use the game's Confirm button to list the item."
            : error;
    }

    public void UpdateExistingListings()
    {
        if (Busy) return;
        if (!CanStartListingItems) { Status = StartListingAvailabilityError!; return; }
        Rows.Clear();
        if (bridge.TryReadSellItem(out _, out _)) { Status = "Close the individual selling window first, leaving the retainer's selling list open."; return; }
        if (!bridge.TryGetSession(out var active, out var error)) { Status = error; return; }
        if (!bridge.TryGetOwnRetainerIds(out _))
        { Status = "Retainer ownership data is not ready. Reopen the retainer selling list and try again; no listing will be repriced until your own stock can be excluded."; return; }
        var items = bridge.ReadExistingListings(out error);
        if (items.Count == 0) { Status = string.IsNullOrEmpty(error) ? "This retainer has no listings." : error; return; }
        var unmarketable = items.Count(item => !marketableItemIds.Contains(item.ItemId));
        var excluded = items.Count(item => IsExcluded(item.ItemId));
        items = items.Where(item => marketableItemIds.Contains(item.ItemId) && !IsExcluded(item.ItemId)).ToList();
        if (items.Count == 0) { Status = "All current listings are excluded or are not marketable."; return; }
        Rows.AddRange(items.Select(x => new PriceRow(x)));
        session = active;
        work = Work.Scan;
        index = 0;
        step = Step.Start;
        cache.Clear();
        workSource = PriceSource.Local;
        Status = $"Automatically checking and updating {items.Count} eligible listing(s) with fresh local marketboard prices ({excluded} excluded, {unmarketable} not marketable).";
    }

    private void TickManual(DateTimeOffset now)
    {
        if (manualTarget is null) { FinishManual("Item lookup stopped."); return; }
        if (bridge.GetHomeWorld()?.WorldId != manualTarget.World.WorldId)
        { FinishManual("Item lookup stopped because the home-world data changed. Search again."); return; }
        if (step == Step.Start)
        {
            deadline = now.AddSeconds(25);
            quoteTask = universalis.FetchAsync(manualTarget.World.WorldId, manualTarget.Item.ItemId, cancellation.Token);
            step = Step.Quote;
        }
        if (quoteTask is { IsCompleted: true } task)
        {
            quoteTask = null;
            try { ManualSnapshot = task.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                FinishManual(ex is OperationCanceledException ? "Price lookup cancelled." : ex.Message);
                return;
            }
            if (ManualSnapshot is null) { FinishManual("Universalis returned no price data."); return; }
            var hasOwnRetainerIds = bridge.TryGetOwnRetainerIds(out var ownRetainerIds);
            ManualProposal = Calculate(ManualSnapshot, manualTarget.Item.ItemId, manualTarget.World.WorldId,
                manualTarget.IsHq, ownRetainerIds);
            if (!hasOwnRetainerIds)
                ManualQuoteWarning = "Your retainer IDs have not loaded, so this read-only quote may include your own listing. Open any retainer list before relying on the undercut price.";
            else if (!ManualProposal.CanApply && ManualProposal.Error?.Contains("did not include its retainer ID", StringComparison.OrdinalIgnoreCase) == true)
                ManualQuoteWarning = "One or more matching listings omit seller identity. Their prices are shown above, but they cannot be excluded safely from an undercut.";
            FinishManual(ManualProposal.CanApply ? "Price retrieved." : ManualProposal.Error!);
            return;
        }
        if (now > deadline)
        {
            cancellation.Cancel();
            FinishManual("Universalis did not respond in time. Try again or choose Local while an item sell window is open.");
        }
    }

    private void FinishManual(string message)
    {
        work = Work.Idle;
        manualTarget = null;
        Status = message;
        ResetRequest();
    }

    private void TickSingle(DateTimeOffset now)
    {
        if (workingItem is null || CurrentItem is null || !SameDialog(workingItem, CurrentItem))
        { Cancel("The selling item changed; the old price lookup was discarded."); return; }
        if (step == Step.Start)
        {
            if (!StartRequest(workingItem, now, out var error)) { Cancel(error); return; }
            step = Step.Quote;
        }
        if (!ReadRequest(workingItem, now, out var snapshot, out var failure)) return;
        work = Work.Idle;
        if (snapshot is null) { Status = failure; return; }
        CurrentSnapshot = snapshot;
        CurrentProposal = Calculate(snapshot, workingItem);
        if (workSource == PriceSource.Local && !bridge.TryCloseCompare(workingItem, out var closeError))
        { Cancel(closeError); return; }
        Status = CurrentProposal.CanApply ? $"Suggested price: {CurrentProposal.SuggestedPrice:N0} gil each." : CurrentProposal.Error!;
        if (autoApply && CurrentProposal.CanApply) FillCurrent();
    }

    private void TickScan(DateTimeOffset now)
    {
        if (index >= Rows.Count)
        {
            var updated = Rows.Count(row => row.Status == "Updated");
            var unchanged = Rows.Count(row => row.Status == "Already priced");
            var skipped = Rows.Count - updated - unchanged;
            Finish($"Existing listing update complete: {updated} updated, {unchanged} already at target, {skipped} left unchanged because a safe price was unavailable.");
            return;
        }
        var row = Rows[index];
        if (step == Step.Start)
        {
            ResetRequest();
            workingItem = row.Item;
            if (workSource == PriceSource.Local)
            {
                if (!bridge.TryOpenExisting(row.Item, out var error)) { ScanFailed(row, error); return; }
                deadline = now.AddSeconds(8);
                step = Step.Opening;
                return;
            }
            if (cache.TryGetValue((row.Item.Session.WorldId, row.Item.ItemId), out var cached))
            { ScanReceived(row, cached); return; }
            if (!StartRequest(row.Item, now, out var failed)) { ScanFailed(row, failed); return; }
            step = Step.Quote;
        }
        if (step == Step.Opening)
        {
            if (!bridge.TryReadSellItem(out var opened, out _))
            { if (now > deadline) Cancel("Stopped because the next sell window did not open in time. Check the retainer UI before resuming."); return; }
            if (!SameListing(row.Item, opened)) { Cancel("The opened item differs from the item being checked."); return; }
            workingItem = opened;
            if (!StartRequest(opened, now, out var error)) { ScanFailed(row, error); return; }
            step = Step.Quote;
        }
        if (step == Step.Quote && workingItem is not null && ReadRequest(workingItem, now, out var snapshot, out var failure))
        {
            if (snapshot is null) ScanFailed(row, failure);
            else ScanReceived(row, snapshot);
        }
        if (step == Step.Closing)
        {
            if (workingItem is not null && row.Proposal is { CanApply: true } proposal
                && proposal.SuggestedPrice != workingItem.CurrentPrice)
            {
                if (!bridge.TryCloseCompare(workingItem, out var closeError))
                { Cancel($"Could not close the price comparison before updating {row.Item.Name}: {closeError}"); return; }
                submittedPrice = proposal.SuggestedPrice;
                if (!bridge.TrySetExistingPrice(workingItem, submittedPrice, out var updateError))
                { Cancel($"Stopped while updating {row.Item.Name}: {updateError}"); return; }
                row.Status = "Submitted; verifying update";
                Status = $"Submitted {row.Item.Name} at {submittedPrice:N0} gil each. Verifying the retainer update.";
                deadline = now.AddSeconds(10);
                step = Step.Confirming;
                return;
            }
            if (workingItem is not null && !bridge.TryClosePriceWindows(workingItem, out var error))
            { Cancel($"Could not close the price window for {row.Item.Name}: {error}"); return; }
            if (row.Proposal is { CanApply: true } unchangedProposal && unchangedProposal.SuggestedPrice == row.Item.CurrentPrice)
                row.Status = "Already priced";
            index++;
            step = Step.Start;
            workingItem = null;
            ResetRequest();
            nextTick = now.AddMilliseconds(650);
        }

        if (step == Step.Confirming)
        {
            var listed = bridge.ReadExistingListings(out var readError);
            var sellWindowOpen = bridge.TryReadSellItem(out _, out _);
            var current = readError.Length == 0 ? listed.FirstOrDefault(item => item.Slot == row.Item.Slot) : null;
            if (!sellWindowOpen && current is not null && SameListingIdentity(row.Item, current)
                && current.CurrentPrice == submittedPrice)
            {
                row.Status = "Updated";
                index++;
                step = Step.Start;
                workingItem = null;
                ResetRequest();
                nextTick = now.AddMilliseconds(750);
                return;
            }
            if (now > deadline)
            {
                row.Status = "Submitted; confirmation not observed";
                Cancel("Stopped because the last existing-listing update could not be confirmed. Check the retainer before running this again.");
            }
        }
    }

    private void TickBatchListing(DateTimeOffset now)
    {
        if (step == Step.Start)
        {
            if (index >= batchCandidates.Count)
            {
                FinishBatchListing($"Automatic listing complete: {listingSucceeded} listed, {listingSkipped} skipped.");
                return;
            }

            var listedBefore = bridge.ReadExistingListings(out var listingError);
            if (listingError.Length != 0) { Cancel($"Automatic listing stopped: {listingError}"); return; }
            if (listedBefore.Count >= 20)
            {
                FinishBatchListing($"Stopped because the retainer has all 20 listing slots filled. {listingSucceeded} listed, {listingSkipped} skipped.");
                return;
            }

            var candidate = batchCandidates[index];
            if (!marketableItemIds.Contains(candidate.ItemId) || IsExcluded(candidate.ItemId))
            { SkipBatchItem("Skipped an item that is now excluded or not marketable."); return; }

            listingCandidate = candidate;
            preListingSlots = listedBefore.Select(row => row.Slot).ToHashSet();
            if (!bridge.TryOpenInventoryItem(candidate, out var openError))
            { Cancel($"Automatic listing stopped before opening {candidate.Name}: {openError}"); return; }
            workingItem = null;
            deadline = now.AddSeconds(8);
            step = Step.Opening;
            return;
        }

        if (step == Step.Opening)
        {
            if (!bridge.TryReadSellItem(out var opened, out _))
            {
                if (now > deadline) Cancel("Automatic listing stopped because the next item sale window did not open in time. No item was skipped; check the retainer UI before retrying.");
                return;
            }
            if (listingCandidate is not { } expected || opened.IsExisting || opened.ItemId != expected.ItemId ||
                opened.IsHq != expected.IsHq || opened.InventoryType != expected.InventoryType || opened.Slot != expected.Slot)
            { Cancel("Automatic listing stopped because the opened item differs from the inventory snapshot. No price was submitted."); return; }

            workingItem = opened;
            if (!StartRequest(opened, now, out var requestError))
            {
                if (requestError.Contains("previous marketboard search is still finishing", StringComparison.OrdinalIgnoreCase))
                {
                    deadline = now.AddSeconds(20);
                    step = Step.WaitingToCompare;
                    Status = $"Waiting for the previous marketboard response before checking {opened.Name}...";
                    return;
                }
                SkipBatchItem($"Skipped {opened.Name}: {requestError}");
                return;
            }
            step = Step.Quote;
            return;
        }

        if (step == Step.WaitingToCompare)
        {
            if (workingItem is not { } waitingItem || listingCandidate is null)
            { Cancel("Automatic listing stopped because the item waiting for a price check was lost."); return; }
            if (!bridge.TryReadSellItem(out var current, out _) || !SameDialog(waitingItem, current))
            { Cancel("Automatic listing stopped because the item sale window changed while the marketboard response was finishing."); return; }
            if (bridge.IsComparisonVisible)
            { Cancel("Automatic listing paused because a market comparison is still open. Close it before starting the batch again."); return; }
            if (bridge.IsLocalSearchBusy)
            {
                if (now > deadline) Cancel("Automatic listing stopped because the previous marketboard response did not finish. Close any market comparison and retry.");
                return;
            }
            if (!StartRequest(current, now, out var retryError))
            {
                if (retryError.Contains("previous marketboard search is still finishing", StringComparison.OrdinalIgnoreCase))
                {
                    if (now > deadline) Cancel("Automatic listing stopped because the previous marketboard response did not finish. Close any market comparison and retry.");
                    return;
                }
                SkipBatchItem($"Skipped {current.Name}: {retryError}");
                return;
            }
            step = Step.Quote;
            return;
        }

        if (step == Step.Quote)
        {
            if (workingItem is not { } current || listingCandidate is null)
            { Cancel("Automatic listing stopped because the active item was lost."); return; }
            if (!ReadRequest(current, now, out var snapshot, out var requestError)) return;
            if (snapshot is null)
            { SkipBatchItem($"Skipped {current.Name}: {requestError}"); return; }

            var proposal = Calculate(snapshot, current);
            if (!proposal.CanApply)
            { SkipBatchItem($"Skipped {current.Name}: {proposal.Error}"); return; }
            if (!bridge.TryCloseCompare(current, out var closeError))
            { Cancel($"Automatic listing stopped before confirming {current.Name}: {closeError}"); return; }

            listingSubmittedPrice = proposal.SuggestedPrice;
            deadline = now.AddSeconds(8);
            step = Step.ClosingListingCompare;
            Status = $"Got a price for {current.Name}. Closing the comparison before confirming the sale.";
            return;
        }

        if (step == Step.ClosingListingCompare)
        {
            if (bridge.IsComparisonVisible || bridge.IsLocalSearchBusy)
            {
                if (now > deadline) Cancel("Automatic listing stopped because the market comparison did not close safely. No sale was confirmed.");
                return;
            }
            if (workingItem is not { } current || !bridge.TryReadSellItem(out var open, out _) || !SameDialog(current, open))
            { Cancel("Automatic listing stopped because the sale window changed before confirmation. No sale was submitted."); return; }
            if (!bridge.TryConfirmNewListing(open, listingSubmittedPrice, out var confirmError))
            { Cancel($"Automatic listing stopped before confirming {open.Name}: {confirmError}"); return; }
            Status = $"Submitted {open.Name} at {listingSubmittedPrice:N0} gil each. Waiting for the retainer list to confirm it.";
            deadline = now.AddSeconds(12);
            step = Step.Confirming;
            return;
        }

        if (step == Step.ClosingSkippedCompare)
        {
            if (bridge.IsComparisonVisible || bridge.IsLocalSearchBusy)
            {
                if (now > deadline) Cancel("Automatic listing stopped because the skipped item's market comparison did not close. Close it manually before retrying.");
                return;
            }
            if (workingItem is { } skipped && bridge.IsSellWindowVisible)
            {
                if (!bridge.TryClosePriceWindows(skipped, out var closeError))
                { Cancel($"Automatic listing stopped while closing the skipped item: {closeError}"); return; }
                step = Step.ClosingSkippedSell;
                deadline = now.AddSeconds(8);
                return;
            }
            CompleteSkippedBatchItem(now);
            return;
        }

        if (step == Step.ClosingSkippedSell)
        {
            if (bridge.IsSellWindowVisible)
            {
                if (now > deadline) Cancel("Automatic listing stopped because the skipped item's sale window did not close. Close it manually before retrying.");
                return;
            }
            CompleteSkippedBatchItem(now);
            return;
        }

        if (step == Step.Confirming)
        {
            if (listingCandidate is not { } expected || workingItem is not { } submitted)
            { Cancel("Automatic listing stopped because its confirmation target was lost."); return; }
            var listed = bridge.ReadExistingListings(out var readError);
            var sellWindowOpen = bridge.TryReadSellItem(out _, out _);
            if (readError.Length == 0 && !sellWindowOpen && listed.Any(row => !preListingSlots.Contains(row.Slot)
                    && row.ItemId == expected.ItemId && row.IsHq == expected.IsHq
                    && row.Quantity == submitted.Quantity && row.CurrentPrice == listingSubmittedPrice))
            {
                listingSucceeded++;
                index++;
                listingCandidate = null;
                workingItem = null;
                step = Step.Start;
                nextTick = now.AddMilliseconds(900);
                Status = $"Listed {submitted.Name} at {listingSubmittedPrice:N0} gil each. Continuing with the next eligible item.";
                return;
            }
            if (now > deadline)
            {
                Cancel("Stopped because the new listing was not confirmed in the retainer list. Check the game before retrying to avoid duplicate listings.");
                return;
            }
        }
    }

    private void SkipBatchItem(string message)
    {
        pendingSkipMessage = message;
        ResetRequest();
        if (workingItem is { } openItem && bridge.IsSellWindowVisible)
        {
            if (bridge.IsComparisonVisible && !bridge.TryCloseCompare(openItem, out var closeError))
            { Cancel($"Automatic listing stopped while closing the skipped item's comparison: {closeError}"); return; }
            deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            step = Step.ClosingSkippedCompare;
            Status = $"{message} Closing its price windows before continuing.";
            return;
        }
        CompleteSkippedBatchItem(DateTimeOffset.UtcNow);
    }

    private void CompleteSkippedBatchItem(DateTimeOffset now)
    {
        var message = pendingSkipMessage ?? "Skipped item; continuing.";
        listingSkipped++;
        index++;
        listingCandidate = null;
        workingItem = null;
        pendingSkipMessage = null;
        step = Step.Start;
        ResetRequest();
        Status = message;
        nextTick = now.AddMilliseconds(650);
    }

    private void FinishBatchListing(string message)
    {
        work = Work.Idle;
        workingItem = null;
        listingCandidate = null;
        batchCandidates.Clear();
        Status = message;
        ResetRequest();
    }

    private void ScanReceived(PriceRow row, PriceSnapshot snapshot)
    {
        row.Snapshot = snapshot;
        row.Proposal = Calculate(snapshot, row.Item);
        row.Status = !row.Proposal.CanApply ? row.Proposal.Error!
            : row.Proposal.SuggestedPrice == row.Item.CurrentPrice ? "Already priced"
            : "Ready";
        cache[(row.Item.Session.WorldId, row.Item.ItemId)] = snapshot;
        if (workSource == PriceSource.Local) step = Step.Closing;
        else { index++; step = Step.Start; nextTick = DateTimeOffset.UtcNow.AddMilliseconds(650); }
    }

    private void ScanFailed(PriceRow row, string error)
    {
        row.Status = error;
        if (workSource == PriceSource.Local && workingItem is { DialogGeneration: > 0 }) step = Step.Closing;
        else { index++; step = Step.Start; }
    }

    private bool StartRequest(SellItem item, DateTimeOffset now, out string error)
    {
        error = "";
        deadline = now.AddSeconds(25);
        if (workSource == PriceSource.Universalis)
            quoteTask = universalis.FetchAsync(item.Session.WorldId, item.ItemId, cancellation.Token);
        else
        {
            if (!bridge.RequestCompare(item, out error)) return false;
            localRequested = true;
        }
        return true;
    }

    // Returns true once a request finishes, including failures; false means still waiting.
    private bool ReadRequest(SellItem item, DateTimeOffset now, out PriceSnapshot? snapshot, out string error)
    {
        snapshot = null;
        error = "";
        if (quoteTask is { IsCompleted: true } task)
        {
            quoteTask = null;
            try { snapshot = task.GetAwaiter().GetResult(); }
            catch (Exception ex) { error = ex is OperationCanceledException ? "Price lookup cancelled." : ex.Message; }
            return true;
        }
        if (localRequested && bridge.TryGetLocalSnapshot(item, out snapshot, out error)) return true;
        if (!string.IsNullOrEmpty(error)) return true;
        if (now <= deadline) return false;
        error = "The price check timed out. Check locally or try again.";
        cancellation.Cancel();
        return true;
    }

    private PriceProposal Calculate(PriceSnapshot snapshot, SellItem item)
        => Calculate(snapshot, item.ItemId, item.Session.WorldId, item.IsHq, bridge.OwnRetainerIds());

    private PriceProposal Calculate(PriceSnapshot snapshot, uint itemId, uint worldId, bool isHq,
        IReadOnlySet<ulong> ownRetainerIds)
        => PriceCalculator.Calculate(snapshot, itemId, worldId, isHq, ownRetainerIds, (uint)config.MinimumPrice,
            DateTimeOffset.UtcNow, config.UseMaximumPriceAge ? TimeSpan.FromMinutes(config.MaximumAgeMinutes) : null);

    private bool IsExcluded(uint itemId) => config.ExcludedItemIds.Contains(itemId);

    private void ResetRequest()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        cancellation = new CancellationTokenSource();
        quoteTask = null;
        localRequested = false;
    }

    private void Finish(string message) { work = Work.Idle; workingItem = null; Status = message; ResetRequest(); }

    public void Cancel(string message = "Stopped. Already submitted price changes remain applied.")
    {
        var wasBatchListing = work == Work.BatchListing;
        ResetRequest();
        work = Work.Idle;
        manualTarget = null;
        if (wasBatchListing)
        {
            batchCandidates.Clear();
            listingCandidate = null;
            workingItem = null;
        }
        Status = message;
    }

    private static bool SameListingIdentity(SellItem a, SellItem b) => a.Session == b.Session && a.ItemId == b.ItemId
        && a.IsHq == b.IsHq && a.Quantity == b.Quantity && a.InventoryType == b.InventoryType && a.Slot == b.Slot;
    private static bool SameListing(SellItem a, SellItem b) => SameListingIdentity(a, b) && a.CurrentPrice == b.CurrentPrice;
    private static bool SameDialog(SellItem a, SellItem b) => SameListing(a, b) && a.DialogGeneration == b.DialogGeneration;

    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
}
