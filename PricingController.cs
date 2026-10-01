namespace RetainerPricer;

internal sealed class PriceRow(SellItem item)
{
    public SellItem Item { get; } = item;
    public PriceSnapshot? Snapshot { get; set; }
    public PriceProposal? Proposal { get; set; }
    public bool Selected { get; set; } = true;
    public string Status { get; set; } = "Waiting";
}

internal sealed class PricingController : IDisposable
{
    private enum Work { Idle, Single, Manual, Scan, Apply }
    private enum Step { Start, Opening, Quote, Closing, Confirming }
    private readonly NativeMarketBridge bridge;
    private readonly UniversalisClient universalis;
    private readonly PluginConfig config;
    private CancellationTokenSource cancellation = new();
    private readonly Dictionary<(uint World, uint Item), PriceSnapshot> cache = [];
    private Task<PriceSnapshot>? quoteTask;
    private Work work;
    private Step step;
    private int index;
    private long seenDialog = -1;
    private DateTimeOffset nextTick;
    private DateTimeOffset deadline;
    private SellItem? workingItem;
    private ManualQuoteTarget? manualTarget;
    private MarketSession? session;
    private bool autoApply;
    private bool localRequested;
    private PriceSource workSource;
    private uint submittedPrice;
    private List<PriceRow> applying = [];

    private sealed record ManualQuoteTarget(ItemChoice Item, bool IsHq, MarketWorld World);

    public PricingController(NativeMarketBridge bridge, UniversalisClient universalis, PluginConfig config)
        => (this.bridge, this.universalis, this.config) = (bridge, universalis, config);

    public SellItem? CurrentItem { get; private set; }
    public PriceSnapshot? CurrentSnapshot { get; private set; }
    public PriceProposal? CurrentProposal { get; private set; }
    public PriceSnapshot? ManualSnapshot { get; private set; }
    public PriceProposal? ManualProposal { get; private set; }
    public string? ManualQuoteWarning { get; private set; }
    public List<PriceRow> Rows { get; } = [];
    public bool Busy => work != Work.Idle;
    public bool CanUpdateExisting => bridge.RetainerAvailabilityError is null;
    public string? ExistingUpdateError => bridge.RetainerAvailabilityError;
    public bool HasRetainer => bridge.TryGetSession(out _, out _);
    public string Status { get; private set; } = "Open a retainer's selling list to begin.";
    public string Progress => work is Work.Scan ? $"Checking {Math.Min(index + 1, Rows.Count)} of {Rows.Count}"
        : work is Work.Apply ? $"Updating {Math.Min(index + 1, applying.Count)} of {applying.Count}" : "";

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
                if (config.AutoPriceNewListings && !item.IsExisting && !IsExcluded(item.ItemId)) CheckCurrent(true);
            }
            return;
        }
        if (work == Work.Single) TickSingle(now);
        else if (work == Work.Scan) TickScan(now);
        else TickApply(now);
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

    public void CheckCurrent(bool fillAutomatically = false)
    {
        if (Busy) return;
        if (!bridge.TryReadSellItem(out var target, out var error)) { Status = error; return; }
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

    public void ScanExisting()
    {
        if (Busy) return;
        if (!CanUpdateExisting) { Status = ExistingUpdateError!; return; }
        if (bridge.TryReadSellItem(out _, out _)) { Status = "Close the individual selling window first, leaving the retainer's selling list open."; return; }
        if (!bridge.TryGetSession(out var active, out var error)) { Status = error; return; }
        var items = bridge.ReadExistingListings(out error);
        if (items.Count == 0) { Status = string.IsNullOrEmpty(error) ? "This retainer has no listings." : error; return; }
        items = items.Where(item => !IsExcluded(item.ItemId)).ToList();
        if (items.Count == 0) { Status = "Every listing on this retainer is in the item exception list."; return; }
        Rows.Clear();
        Rows.AddRange(items.Select(x => new PriceRow(x)));
        session = active;
        work = Work.Scan;
        index = 0;
        step = Step.Start;
        cache.Clear();
        workSource = config.Source;
        Status = "Checking existing listings. Review the suggested prices before applying.";
    }

    public void ApplyReviewed()
    {
        if (Busy) return;
        if (!CanUpdateExisting) { Status = ExistingUpdateError!; return; }
        if (bridge.TryReadSellItem(out _, out _)) { Status = "Close the individual selling window before updating the reviewed listings."; return; }
        applying = Rows.Where(x => x.Selected && !IsExcluded(x.Item.ItemId) && x.Proposal is { CanApply: true }
            && x.Proposal.SuggestedPrice != x.Item.CurrentPrice
            && (!config.OnlyLowerExistingPrices || x.Proposal.SuggestedPrice < x.Item.CurrentPrice)).ToList();
        if (applying.Count == 0) { Status = "No selected price changes to apply."; return; }
        if (!bridge.TryGetSession(out var active, out var error) || applying.Any(x => x.Item.Session != active))
        { Status = string.IsNullOrEmpty(error) ? "The reviewed retainer changed. Check existing listings again." : error; return; }
        session = active;
        work = Work.Apply;
        index = 0;
        step = Step.Start;
        Status = "Applying the selected prices. Keep this retainer's selling list open.";
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
        if (index >= Rows.Count) { Finish("Price check complete. Review the table and apply your selected changes."); return; }
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
            if (workingItem is not null && !bridge.TryClosePriceWindows(workingItem, out var error))
            { Cancel(error); return; }
            index++;
            step = Step.Start;
            nextTick = now.AddMilliseconds(650);
        }
    }

    private void ScanReceived(PriceRow row, PriceSnapshot snapshot)
    {
        row.Snapshot = snapshot;
        row.Proposal = Calculate(snapshot, row.Item);
        row.Status = !row.Proposal.CanApply ? row.Proposal.Error!
            : row.Proposal.SuggestedPrice == row.Item.CurrentPrice ? "Already priced"
            : config.OnlyLowerExistingPrices && row.Proposal.SuggestedPrice > row.Item.CurrentPrice ? "Already lower"
            : "Ready";
        row.Selected = row.Status == "Ready";
        cache[(row.Item.Session.WorldId, row.Item.ItemId)] = snapshot;
        if (workSource == PriceSource.Local) step = Step.Closing;
        else { index++; step = Step.Start; nextTick = DateTimeOffset.UtcNow.AddMilliseconds(650); }
    }

    private void ScanFailed(PriceRow row, string error)
    {
        row.Status = error;
        row.Selected = false;
        if (workSource == PriceSource.Local && workingItem is { DialogGeneration: > 0 }) step = Step.Closing;
        else { index++; step = Step.Start; }
    }

    private void TickApply(DateTimeOffset now)
    {
        if (index >= applying.Count) { Finish("Finished processing the reviewed listings. See each row for its result."); return; }
        var row = applying[index];
        if (step == Step.Start)
        {
            if (row.Snapshot is null) { ApplyFailed(row, "No price data."); return; }
            var recalculated = Calculate(row.Snapshot, row.Item);
            if (!recalculated.CanApply || recalculated.SuggestedPrice != row.Proposal?.SuggestedPrice)
            { ApplyFailed(row, recalculated.Error ?? "Suggested price changed. Check again."); return; }
            if (!bridge.TryOpenExisting(row.Item, out var error)) { ApplyFailed(row, error); return; }
            deadline = now.AddSeconds(8);
            step = Step.Opening;
            return;
        }
        if (step == Step.Opening)
        {
            if (!bridge.TryReadSellItem(out var opened, out _))
            { if (now > deadline) Cancel("Stopped because the listing window did not open in time. Check the retainer UI before resuming."); return; }
            if (row.Snapshot is null) { Cancel("Stopped because this listing has no reviewed price data."); return; }
            var fresh = Calculate(row.Snapshot, row.Item);
            if (!fresh.CanApply || fresh.SuggestedPrice != row.Proposal?.SuggestedPrice)
            { Cancel(fresh.Error ?? "The reviewed price changed while the listing window opened. Review the prices again."); return; }
            if (!SameListing(row.Item, opened)) { Cancel("The listing changed during the update. Review the current retainer's stock again."); return; }
            workingItem = opened;
            submittedPrice = row.Proposal!.SuggestedPrice;
            if (!bridge.TrySetExistingPrice(opened, submittedPrice, out var error)) { Cancel(error); return; }
            row.Status = "Submitted; waiting for listing update";
            step = Step.Confirming;
            deadline = now.AddSeconds(10);
            return;
        }
        if (step == Step.Confirming)
        {
            var current = bridge.ReadExistingListings(out _).FirstOrDefault(x => x.Slot == row.Item.Slot);
            if (current is not null && SameListingIdentity(row.Item, current) && current.CurrentPrice == submittedPrice
                && !bridge.TryReadSellItem(out _, out _))
            { row.Status = "Updated"; row.Selected = false; index++; step = Step.Start; nextTick = now.AddMilliseconds(750); }
            else if (now > deadline) { row.Status = "Submitted; confirmation not observed"; Cancel("Stopped because the last listing update could not be confirmed. Check it in the game before retrying."); }
        }
    }

    private void ApplyFailed(PriceRow row, string error)
    { row.Status = error; row.Selected = false; index++; step = Step.Start; }

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
        ResetRequest();
        work = Work.Idle;
        manualTarget = null;
        Status = message;
    }

    private static bool SameListingIdentity(SellItem a, SellItem b) => a.Session == b.Session && a.ItemId == b.ItemId
        && a.IsHq == b.IsHq && a.Quantity == b.Quantity && a.InventoryType == b.InventoryType && a.Slot == b.Slot;
    private static bool SameListing(SellItem a, SellItem b) => SameListingIdentity(a, b) && a.CurrentPrice == b.CurrentPrice;
    private static bool SameDialog(SellItem a, SellItem b) => SameListing(a, b) && a.DialogGeneration == b.DialogGeneration;

    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
}
