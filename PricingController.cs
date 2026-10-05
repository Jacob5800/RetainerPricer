namespace RetainerPricer;

internal sealed class PriceRow(SellItem item)
{
    public SellItem Item { get; } = item;
    public PriceSnapshot? Snapshot { get; set; }
    public PriceProposal? Proposal { get; set; }
    public string Status { get; set; } = "Waiting";
    public bool AwaitingPriceDropDecision { get; set; }
    public bool PriceDropApproved { get; set; }
    public int PriceDropReviewThresholdPercent { get; set; }
}

internal sealed class PricingController : IDisposable
{
    private enum Work { Idle, Single, Manual, Scan, AutoUpdateAllRetainers, BatchListing }
    private enum Step
    {
        Start, Opening, WaitingToCompare, Quote, Closing, ClosingListingCompare, ClosingSkippedCompare,
        ClosingSkippedSell, Confirming, AutoStartRetainer, AutoCloseSellList, AutoWaitRetainerMenu,
        AutoWaitPicker, AutoSelectRetainer, AutoWaitTargetMenu, AutoSelectSellMenu, AutoWaitSellList
    }
    private readonly NativeMarketBridge bridge;
    private readonly UniversalisClient universalis;
    private readonly PluginConfig config;
    private readonly IReadOnlySet<uint> marketableItemIds;
    private CancellationTokenSource cancellation = new();
    private Task<PriceSnapshot>? quoteTask;
    private Work work;
    private Step step;
    private int index;
    private long seenDialog = -1;
    private DateTimeOffset nextTick;
    private DateTimeOffset deadline;
    private string? pendingSkipMessage;
    private bool suppressAutoPriceUntilSellWindowCloses;
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
    private uint requestedListingQuantity;
    private int listingSucceeded;
    private int listingSkipped;
    private bool batchSellingOnly;
    private readonly Dictionary<uint, uint> batchSoldQuantitiesByItemId = [];
    private readonly HashSet<uint> batchMaximumReachedItemIds = [];
    private readonly List<RetainerIdentity> autoRetainers = [];
    private int autoRetainerIndex;
    private int autoRetainersCompleted;
    private int autoUnavailableRetainers;
    private int autoEmptyRetainers;
    private int autoUpdated;
    private int autoAlreadyPriced;
    private int autoSkipped;
    private ulong autoContentId;
    private uint autoWorldId;
    private ulong autoLastSelectedRetainerId;
    private int autoRetainerDialogueClicks;
    private DateTimeOffset autoRetainerNextDialogueClick;

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
    public PriceRow? PriceDropReviewItem => index >= Rows.Count && (work is Work.Scan or Work.AutoUpdateAllRetainers)
        ? Rows.FirstOrDefault(row => row.AwaitingPriceDropDecision)
        : null;
    public bool Busy => work != Work.Idle;
    public bool IsListingItemsRunning => work == Work.BatchListing;
    public bool IsBatchSellingOnlyRunning => work == Work.BatchListing && batchSellingOnly;
    public bool IsUpdatingListings => work is Work.Scan or Work.AutoUpdateAllRetainers;
    public bool IsAutoUpdatingAllRetainers => work == Work.AutoUpdateAllRetainers;
    public bool CanUpdateExisting => bridge.RetainerAvailabilityError is null;
    public bool CanApplyExisting => CanUpdateExisting && bridge.ItemSelectorAvailabilityError is null;
    public bool CanStartListingItems => CanApplyExisting;
    public string? ExistingUpdateError => bridge.RetainerAvailabilityError;
    public string? ExistingApplyError => bridge.ItemSelectorAvailabilityError;
    public string? StartListingAvailabilityError => ExistingUpdateError ?? ExistingApplyError;
    public bool HasRetainer => bridge.TryGetSession(out _, out _);
    public string Status { get; private set; } = "Open a retainer's selling list to begin.";
    public string Progress
    {
        get
        {
            if (work == Work.Scan) return $"Checking {Math.Min(index + 1, Rows.Count)} of {Rows.Count}";
            if (work == Work.AutoUpdateAllRetainers)
            {
                var retainerName = autoRetainers.Count > autoRetainerIndex
                    ? autoRetainers[autoRetainerIndex].Name : "Retainers";
                return step is Step.AutoStartRetainer or Step.AutoCloseSellList or Step.AutoWaitRetainerMenu or
                    Step.AutoWaitPicker or Step.AutoSelectRetainer or Step.AutoWaitTargetMenu or
                    Step.AutoSelectSellMenu or Step.AutoWaitSellList
                    ? $"Retainer {Math.Min(autoRetainerIndex + 1, autoRetainers.Count)} of {autoRetainers.Count}: {retainerName}"
                    : $"Retainer {Math.Min(autoRetainerIndex + 1, autoRetainers.Count)} of {autoRetainers.Count}: {retainerName} · listing {Math.Min(index + 1, Rows.Count)} of {Rows.Count}";
            }
            if (work == Work.BatchListing) return $"Listing {Math.Min(index + 1, batchCandidates.Count)} of {batchCandidates.Count}";
            return "";
        }
    }

    public void Update()
    {
        var now = DateTimeOffset.UtcNow;
        if (work != Work.Idle)
        {
            if (bridge.IsClientStateUnavailable)
            {
                Cancel("Stopped because the character disconnected, began loading, or started logging out.");
                return;
            }
            if (work == Work.AutoUpdateAllRetainers && bridge.IsCharacterOrWorldChanged(autoContentId, autoWorldId))
            {
                Cancel("Stopped because the character or home world changed while moving between retainers.");
                return;
            }
            var autoNavigation = work == Work.AutoUpdateAllRetainers && step is Step.AutoCloseSellList or
                Step.AutoWaitRetainerMenu or Step.AutoWaitPicker or Step.AutoSelectRetainer or
                Step.AutoWaitTargetMenu or Step.AutoSelectSellMenu or Step.AutoWaitSellList;
            if (work != Work.Manual && !autoNavigation && session is not null && bridge.IsSessionIdentityChanged(session))
            {
                Cancel("Stopped because the character, world, or active retainer changed.");
                return;
            }
            // RetainerSell can briefly become hidden while the game transitions from its selling list
            // to the item form. Step-specific reads and submit methods revalidate the exact item dialog
            // before changing a price, so don't abort the whole operation on visibility alone.
        }
        if (now < nextTick) return;
        nextTick = now.AddMilliseconds(250);
        CurrentItem = bridge.TryReadSellItem(out var selected, out _) ? selected : null;
        if (work == Work.Manual) { TickManual(now); return; }
        if (work == Work.Idle)
        {
            if (CurrentItem is not { } item)
            {
                if (!bridge.IsSellWindowVisible) suppressAutoPriceUntilSellWindowCloses = false;
                return;
            }
            if (suppressAutoPriceUntilSellWindowCloses) return;
            if (item.DialogGeneration != seenDialog)
            {
                seenDialog = item.DialogGeneration;
                CurrentSnapshot = null;
                CurrentProposal = null;
                if (config.AutoPriceNewListings && !item.IsExisting && marketableItemIds.Contains(item.ItemId))
                    CheckCurrent(true);
            }
            return;
        }
        if (work == Work.Single) TickSingle(now);
        else if (work == Work.Scan) TickScan(now);
        else if (work == Work.AutoUpdateAllRetainers) TickAutoUpdateAllRetainers(now);
        else if (work == Work.BatchListing) TickBatchListing(now);
    }

    public void CheckManualItem(ItemChoice item, bool isHq, MarketWorld world)
    {
        if (Busy) return;
        if (item.ItemId == 0 || world.WorldId == 0) { Status = "Choose a valid item and wait for your home-world data."; return; }
        if ((config.UseDataCenterPrices || config.UseRegionPrices) && string.IsNullOrWhiteSpace(world.DataCenterName))
        { Status = "Could not identify your home world's Data Center. No price lookup was started."; return; }
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
        InventoryCandidates.Clear();
        InventorySnapshotAt = null;
        if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var excludedItemIds, out _, out var protectionError))
        {
            InventorySnapshotError = protectionError;
            Status = protectionError;
            return;
        }
        var items = bridge.ReadCarriedInventory(marketableItemIds, excludedItemIds,
            out var exceptionSkipped, out var unmarketableSkipped, out var error);
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
        if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var excludedItemIds, out _, out var protectionError))
        {
            ListedCandidates.Clear();
            ListedSnapshotAt = null;
            ListedSnapshotError = protectionError;
            Status = protectionError;
            return;
        }
        var items = bridge.ReadExistingListings(out var error);
        ListedCandidates.Clear();
        ListedExceptionSkipped = items.Count(item => excludedItemIds.Contains(item.ItemId));
        ListedUnmarketableSkipped = items.Count(item => !marketableItemIds.Contains(item.ItemId));
        if (error.Length == 0)
            ListedCandidates.AddRange(items.Where(item => marketableItemIds.Contains(item.ItemId) && !excludedItemIds.Contains(item.ItemId)));
        ListedSnapshotError = error.Length == 0 ? null : error;
        ListedSnapshotAt = error.Length == 0 ? DateTimeOffset.Now : null;
        Status = error.Length != 0 ? error :
            $"Retainer listing snapshot ready: {ListedCandidates.Count} marketable item(s), {ListedExceptionSkipped} excluded, {ListedUnmarketableSkipped} not marketable.";
    }

    public void StartListingItems() => StartListingItems(batchOnly: false);

    public void StartBatchSellingOnly() => StartListingItems(batchOnly: true);

    private void StartListingItems(bool batchOnly)
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
        var candidates = batchOnly
            ? InventoryCandidates.Where(item => config.BatchSaleQuantities.ContainsKey(item.ItemId)).ToList()
            : InventoryCandidates.ToList();
        if (batchOnly && candidates.Count == 0)
        {
            Status = "No configured Batch selling items were found in carried inventory. Add items in the Batch selling tab, and make sure they are not excluded.";
            return;
        }
        session = active;
        batchCandidates = candidates;
        batchSellingOnly = batchOnly;
        index = 0;
        listingSucceeded = 0;
        listingSkipped = 0;
        listingCandidate = null;
        requestedListingQuantity = 0;
        batchSoldQuantitiesByItemId.Clear();
        batchMaximumReachedItemIds.Clear();
        step = Step.Start;
        workSource = PriceSource.Universalis;
        work = Work.BatchListing;
        suppressAutoPriceUntilSellWindowCloses = false;
        var scope = batchOnly ? "configured Batch selling item stack(s) only" : "eligible stack(s)";
        Status = $"Automatically listing {batchCandidates.Count} {scope} using Universalis. Each item needs a current competing listing and a sale from the last 20 days.";
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
        Status = config.AutoPriceNewListings
            ? $"Opened {item.Name}{(item.IsHq ? " (HQ)" : " (NQ)")} for automatic pricing and listing."
            : $"Opened {item.Name}{(item.IsHq ? " (HQ)" : " (NQ)")} for listing. Confirm the sale in game.";
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
        if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var excludedItemIds, out var savedGearsetItemIds, out var protectionError))
        { Status = protectionError; return; }
        if (excludedItemIds.Contains(target.ItemId))
        { Status = $"{target.Name}: {ItemProtection.Reason(target.ItemId, config, savedGearsetItemIds)}"; return; }
        if (target.IsExisting && IsNoReprice(target.ItemId))
        { CurrentSnapshot = null; CurrentProposal = null; Status = $"{target.Name} is protected by Don't reprice. The plugin will not change its existing price."; return; }
        if (fillAutomatically)
        {
            if (!bridge.TryGetOwnRetainerIds(out _))
            { Status = "Automatic listing stopped because your retainer IDs are not ready. Open the retainer selling list and try again; no sale was submitted."; return; }
            var currentListings = bridge.ReadExistingListings(out var listingsError);
            if (listingsError.Length > 0)
            { Status = $"Automatic listing stopped because the current retainer stock could not be verified: {listingsError}"; return; }
            preListingSlots = currentListings.Select(item => item.Slot).ToHashSet();
        }
        workingItem = target;
        session = target.Session;
        CurrentItem = target;
        CurrentSnapshot = null;
        CurrentProposal = null;
        autoApply = fillAutomatically;
        workSource = fillAutomatically ? PriceSource.Universalis : config.Source;
        ResetRequest();
        work = Work.Single;
        step = Step.Start;
        Status = $"Checking {target.Name} on {target.Session.WorldName}...";
    }

    public void FillCurrent()
    {
        if (Busy || workingItem is null || CurrentSnapshot is null) return;
        if (workingItem.IsExisting && IsNoReprice(workingItem.ItemId))
        { Status = $"{workingItem.Name} is protected by Don't reprice. The plugin will not change its existing price."; return; }
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
        if (!TryBeginExistingListingScan(active, out var total, out var excluded, out var protectedCount, out var unmarketable, out error))
        { Status = error; return; }
        if (total == 0) { Status = "This retainer has no listings."; return; }
        if (Rows.Count == 0) { Status = "All current listings are excluded, protected from repricing, or not marketable."; return; }
        work = Work.Scan;
        Status = $"Automatically checking and updating {Rows.Count} eligible listing(s) with Universalis ({excluded} excluded, {protectedCount} protected from repricing, {unmarketable} not marketable). Items without a competing listing or a sale in the last 20 days will be left unchanged.";
    }

    public void ApprovePriceDrop()
    {
        var row = PriceDropReviewItem;
        if (row is null) return;
        row.AwaitingPriceDropDecision = false;
        row.PriceDropApproved = true;
        row.Proposal = null;
        row.Snapshot = null;
        row.Status = "Approved; checking the price again before repricing.";
        index = Rows.IndexOf(row);
        step = Step.Start;
        workingItem = null;
        ResetRequest();
        Status = $"Rechecking {row.Item.Name} after your approval. The plugin will apply only the current verified quote.";
    }

    public void IgnorePriceDrop()
    {
        var row = PriceDropReviewItem;
        if (row is null) return;
        row.AwaitingPriceDropDecision = false;
        row.Status = "Ignored after price-drop review; listing left unchanged.";
        Status = $"Ignored the large price drop for {row.Item.Name}; its listing was left unchanged.";
    }

    public void AutoUpdateAllRetainers()
    {
        if (Busy) return;
        if (!CanStartListingItems) { Status = StartListingAvailabilityError!; return; }
        if (bridge.TryReadSellItem(out _, out _))
        { Status = "Close the individual selling window first, leaving the retainer's selling list open."; return; }
        if (bridge.IsSellWindowVisible || bridge.IsComparisonVisible || bridge.IsRetainerMenuVisible)
        { Status = "Close the item, market comparison, or retainer option window before starting Auto update."; return; }
        if (!bridge.TryGetCharacterContext(out var contentId, out var worldId, out var error))
        { Status = error; return; }
        if (!bridge.TryGetOwnRetainerIds(out _))
        { Status = "Retainer ownership data is not ready. Reopen the retainer menu and try again; no prices will be changed until every retainer can be identified."; return; }

        autoRetainers.Clear();
        var startedAtPicker = bridge.IsRetainerPickerVisible;
        if (startedAtPicker)
        {
            if (!bridge.TryGetRetainerPickerOrder(out var pickerOrder, out error))
            { Status = $"Could not read the retainer picker safely: {error}"; return; }
            autoRetainers.AddRange(pickerOrder);
            if (autoRetainers.Count == 0)
            { Status = "The retainer picker did not contain any retainers."; return; }
            autoContentId = contentId;
            autoWorldId = worldId;
            autoLastSelectedRetainerId = bridge.TryGetSelectedRetainerId(out var selectedId) ? selectedId : 0;
            session = null;
            step = Step.AutoSelectRetainer;
        }
        else
        {
            if (!bridge.IsRetainerSellListVisible)
            { Status = "Open the retainer picker or a retainer's selling list before starting Auto update."; return; }
            if (!bridge.TryGetSession(out var active, out error)) { Status = error; return; }
            if (!bridge.TryGetOwnRetainers(out var retainers))
            { Status = "Retainer ownership data is not ready. Reopen the retainer selling list and try again; no prices will be changed until every retainer can be identified."; return; }

            var activeIndex = -1;
            for (var i = 0; i < retainers.Count; i++)
                if (retainers[i].RetainerId == active.RetainerId) { activeIndex = i; break; }
            if (activeIndex < 0)
            { Status = "The open retainer could not be matched to your retainer roster."; return; }

            autoRetainers.Add(retainers[activeIndex]);
            autoRetainers.AddRange(retainers.Where((_, i) => i != activeIndex));
            autoContentId = active.ContentId;
            autoWorldId = active.WorldId;
            autoLastSelectedRetainerId = active.RetainerId;
            session = active;
            step = Step.AutoStartRetainer;
        }

        autoRetainerIndex = 0;
        autoRetainerDialogueClicks = 0;
        autoRetainerNextDialogueClick = DateTimeOffset.MinValue;
        autoRetainersCompleted = autoUnavailableRetainers = autoEmptyRetainers = 0;
        autoUpdated = autoAlreadyPriced = autoSkipped = 0;
        workSource = PriceSource.Universalis;
        work = Work.AutoUpdateAllRetainers;
        nextTick = DateTimeOffset.UtcNow;
        var startDescription = startedAtPicker ? "top-to-bottom from the retainer picker" : $"with {autoRetainers[0].Name}";
        Status = $"Auto update queued for {autoRetainers.Count} retainer(s), starting {startDescription}.";
    }

    private bool TryBeginExistingListingScan(MarketSession active, out int total, out int excluded,
        out int protectedCount, out int unmarketable, out string error)
    {
        Rows.Clear();
        index = 0;
        workingItem = null;
        total = excluded = protectedCount = unmarketable = 0;
        if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var excludedItemIds, out _, out error)) return false;
        var listings = bridge.ReadExistingListings(out error);
        if (error.Length != 0) return false;
        if (listings.Any(item => item.Session.RetainerId != active.RetainerId || item.Session.ContentId != active.ContentId))
        { error = "The retainer changed while its listings were being read."; return false; }

        total = listings.Count;
        excluded = listings.Count(item => excludedItemIds.Contains(item.ItemId));
        protectedCount = listings.Count(item => !excludedItemIds.Contains(item.ItemId) && IsNoReprice(item.ItemId));
        unmarketable = listings.Count(item => !excludedItemIds.Contains(item.ItemId) && !IsNoReprice(item.ItemId) && !marketableItemIds.Contains(item.ItemId));
        Rows.AddRange(listings.Where(item => marketableItemIds.Contains(item.ItemId) && !excludedItemIds.Contains(item.ItemId) && !IsNoReprice(item.ItemId))
            .Select(item => new PriceRow(item)));
        session = active;
        step = Step.Start;
        workSource = PriceSource.Universalis;
        error = string.Empty;
        return true;
    }

    private void TickAutoUpdateAllRetainers(DateTimeOffset now)
    {
        var error = string.Empty;
        if (step is Step.Start or Step.Opening or Step.WaitingToCompare or Step.Quote or Step.Closing or
            Step.ClosingListingCompare or Step.ClosingSkippedCompare or Step.ClosingSkippedSell or Step.Confirming)
        {
            TickScan(now);
            return;
        }

        switch (step)
        {
            case Step.AutoStartRetainer:
            {
                if (autoRetainerIndex >= autoRetainers.Count) { FinishAutoUpdate(); return; }
                var target = autoRetainers[autoRetainerIndex];
                if (!bridge.TryGetSession(out var active, out error) || active.RetainerId != target.RetainerId || active.ContentId != autoContentId)
                {
                    Cancel(error.Length != 0 ? $"Auto update stopped: {error}" :
                        "Auto update stopped because the open selling list did not match the queued retainer. No other retainer was changed.");
                    return;
                }
                if (!TryBeginExistingListingScan(active, out var total, out var excluded, out var protectedCount, out var unmarketable, out error))
                { Cancel($"Auto update stopped while reading {target.Name}'s listings: {error}"); return; }
                autoSkipped += excluded + protectedCount + unmarketable;
                Status = $"Auto update · {target.Name}: {Rows.Count} eligible listing(s), {excluded} excluded, {protectedCount} protected from repricing, {unmarketable} not marketable.";
                if (total == 0 || Rows.Count == 0)
                {
                    autoRetainersCompleted++;
                    autoEmptyRetainers++;
                    step = Step.AutoCloseSellList;
                    deadline = now.AddSeconds(10);
                    return;
                }
                return;
            }
            case Step.AutoCloseSellList:
                if (session is null || session.RetainerId != autoRetainers[autoRetainerIndex].RetainerId ||
                    !bridge.TryCloseRetainerSellList(session, out error))
                { Cancel($"Auto update stopped before leaving {autoRetainers[autoRetainerIndex].Name}'s selling list: {error}"); return; }
                step = Step.AutoWaitRetainerMenu;
                deadline = now.AddSeconds(10);
                Status = $"Auto update · returning from {autoRetainers[autoRetainerIndex].Name}'s selling list...";
                return;
            case Step.AutoWaitRetainerMenu:
                if (bridge.IsRetainerMenuVisible)
                {
                    if (!bridge.TryGetSelectedRetainerId(out var menuRetainerId) || menuRetainerId != autoRetainers[autoRetainerIndex].RetainerId)
                    { Cancel("Auto update stopped because the retainer menu did not belong to the retainer just processed."); return; }
                    if (!bridge.TrySelectRetainerMenuEntry(text => text.Trim().TrimEnd('.', '…').Equals("Quit", StringComparison.OrdinalIgnoreCase), out _, out error))
                    { Cancel($"Auto update stopped at the retainer menu: {error}"); return; }
                    session = null;
                    step = Step.AutoWaitPicker;
                    deadline = now.AddSeconds(10);
                    return;
                }
                if (now > deadline) Cancel("Auto update stopped because the retainer option menu did not appear after closing the sale list.");
                return;
            case Step.AutoWaitPicker:
                if (bridge.IsRetainerPickerVisible && !bridge.IsRetainerMenuVisible)
                {
                    autoRetainerIndex++;
                    if (autoRetainerIndex >= autoRetainers.Count) { FinishAutoUpdate(); return; }
                    step = Step.AutoSelectRetainer;
                    return;
                }
                if (now > deadline) Cancel("Auto update stopped because the retainer picker did not appear after choosing Quit.");
                return;
            case Step.AutoSelectRetainer:
            {
                var target = autoRetainers[autoRetainerIndex];
                if (!bridge.TrySelectRetainerById(target, out var unavailable, out error))
                {
                    if (!unavailable) { Cancel($"Auto update stopped before selecting {target.Name}: {error}"); return; }
                    autoUnavailableRetainers++;
                    autoRetainersCompleted++;
                    autoRetainerIndex++;
                    Status = $"Auto update · skipped {target.Name}: retainer is not currently available.";
                    if (autoRetainerIndex >= autoRetainers.Count) FinishAutoUpdate();
                    return;
                }
                autoRetainerDialogueClicks = 0;
                autoRetainerNextDialogueClick = DateTimeOffset.MinValue;
                step = Step.AutoWaitTargetMenu;
                deadline = now.AddSeconds(10);
                Status = $"Auto update · opening {target.Name}...";
                return;
            }
            case Step.AutoWaitTargetMenu:
            {
                var expected = autoRetainers[autoRetainerIndex];
                if (bridge.TryGetSelectedRetainerId(out var selectedId) && selectedId != 0 && selectedId != expected.RetainerId &&
                    autoLastSelectedRetainerId != 0 &&
                    selectedId != autoLastSelectedRetainerId)
                { Cancel("Auto update stopped because the game selected a different retainer than the queued one."); return; }
                if (selectedId == expected.RetainerId && bridge.IsRetainerMenuVisible && !bridge.IsRetainerPickerVisible)
                {
                    autoLastSelectedRetainerId = expected.RetainerId;
                    step = Step.AutoSelectSellMenu;
                    return;
                }
                if (bridge.IsRetainerDialogueVisible && !bridge.IsRetainerPickerVisible)
                {
                    if (autoRetainerDialogueClicks < 4 && now >= autoRetainerNextDialogueClick)
                    {
                        if (!bridge.TryAdvanceRetainerDialogue(expected, autoLastSelectedRetainerId, out error))
                        { Cancel($"Auto update stopped while advancing {expected.Name}'s retainer dialogue: {error}"); return; }
                        autoRetainerDialogueClicks++;
                        autoRetainerNextDialogueClick = now.AddMilliseconds(500);
                        Status = $"Auto update · confirming {expected.Name}'s greeting ({autoRetainerDialogueClicks}/4)...";
                    }
                    else if (autoRetainerDialogueClicks >= 4 && now > deadline)
                        Cancel($"Auto update stopped because {expected.Name}'s greeting did not advance to the retainer menu.");
                    return;
                }
                if (now > deadline) Cancel($"Auto update stopped because {expected.Name}'s retainer menu did not appear.");
                return;
            }
            case Step.AutoSelectSellMenu:
            {
                var expected = autoRetainers[autoRetainerIndex];
                if (!bridge.TryGetSelectedRetainerId(out var selectedId) || selectedId != expected.RetainerId)
                { Cancel("Auto update stopped because the retainer changed before opening its listing menu."); return; }
                if (!bridge.TrySelectRetainerMenuEntry(text =>
                        text.Contains("sell", StringComparison.OrdinalIgnoreCase) &&
                        text.Contains("retainer", StringComparison.OrdinalIgnoreCase) &&
                        text.Contains("market", StringComparison.OrdinalIgnoreCase), out _, out error))
                { Cancel($"Auto update stopped at {expected.Name}'s option menu: {error}"); return; }
                step = Step.AutoWaitSellList;
                deadline = now.AddSeconds(12);
                return;
            }
            case Step.AutoWaitSellList:
            {
                var expected = autoRetainers[autoRetainerIndex];
                if (bridge.TryGetSession(out var opened, out _) && opened.RetainerId == expected.RetainerId && opened.ContentId == autoContentId)
                {
                    if (opened.WorldId != autoWorldId)
                    { Cancel("Auto update stopped because the active world changed while opening the next retainer."); return; }
                    session = opened;
                    step = Step.AutoStartRetainer;
                    return;
                }
                if (bridge.TryGetSelectedRetainerId(out var selected) && selected != expected.RetainerId && selected != autoLastSelectedRetainerId)
                { Cancel("Auto update stopped because the opened sale list belongs to an unexpected retainer."); return; }
                if (now > deadline) Cancel($"Auto update stopped because {expected.Name}'s selling list did not open.");
                return;
            }
        }
    }

    private void FinishAutoUpdate()
    {
        var message = $"Auto update complete: {autoRetainersCompleted} retainer(s), {autoUpdated} listing(s) repriced, " +
            $"{autoAlreadyPriced} already at target, {autoSkipped} left unchanged, {autoUnavailableRetainers} unavailable, {autoEmptyRetainers} with no eligible listings.";
        autoRetainers.Clear();
        autoRetainerIndex = 0;
        Finish(message);
    }

    private void TickManual(DateTimeOffset now)
    {
        if (manualTarget is null) { FinishManual("Item lookup stopped."); return; }
        if (bridge.GetHomeWorld()?.WorldId != manualTarget.World.WorldId)
        { FinishManual("Item lookup stopped because the home-world data changed. Search again."); return; }
        if (step == Step.Start)
        {
            deadline = now.AddSeconds(25);
            quoteTask = universalis.FetchAsync(manualTarget.World.WorldId, manualTarget.Item.ItemId,
                cancellation.Token, config.UniversalisCacheMinutes,
                config.UseDataCenterPrices || config.UseRegionPrices ? manualTarget.World.DataCenterName : null,
                config.UseRegionPrices);
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
                manualTarget.IsHq, ownRetainerIds,
                config.UseDataCenterPrices || config.UseRegionPrices ? manualTarget.World.DataCenterName : null);
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
            FinishManual("Universalis did not respond in time. Try again later.");
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
        if (step == Step.Confirming)
        {
            TickSingleConfirmation(now);
            return;
        }
        if (workingItem is null || CurrentItem is null || !SameDialog(workingItem, CurrentItem))
        { Cancel("The selling item changed; the old price lookup was discarded."); return; }
        if (step == Step.Start)
        {
            if (!StartRequest(workingItem, now, out var error)) { Cancel(error); return; }
            step = Step.Quote;
        }
        if (!ReadRequest(workingItem, now, out var snapshot, out var failure)) return;
        if (snapshot is null)
        {
            work = Work.Idle;
            autoApply = false;
            Status = failure;
            return;
        }
        CurrentSnapshot = snapshot;
        CurrentProposal = Calculate(snapshot, workingItem);
        if (workSource == PriceSource.Local && !bridge.TryCloseCompare(workingItem, out var closeError))
        { Cancel(closeError); return; }
        if (!CurrentProposal.CanApply)
        {
            work = Work.Idle;
            autoApply = false;
            Status = CurrentProposal.Error!;
            return;
        }
        if (!autoApply)
        {
            work = Work.Idle;
            Status = $"Suggested price: {CurrentProposal.SuggestedPrice:N0} gil each.";
            return;
        }

        listingSubmittedPrice = CurrentProposal.SuggestedPrice;
        if (!bridge.TryConfirmNewListing(workingItem, listingSubmittedPrice, out var confirmError))
        { Cancel($"Automatic listing stopped before confirming {workingItem.Name}: {confirmError}"); return; }
        deadline = now.AddSeconds(12);
        step = Step.Confirming;
        Status = $"Submitted {workingItem.Name} at {listingSubmittedPrice:N0} gil each. Waiting for the retainer list to confirm it.";
    }

    private void TickSingleConfirmation(DateTimeOffset now)
    {
        if (workingItem is not { } submitted)
        { Cancel("Automatic listing stopped because its confirmation target was lost."); return; }

        var sellWindowOpen = bridge.IsSellWindowVisible;
        if (sellWindowOpen && bridge.TryReadSellItem(out var open, out _) && !SameDialog(submitted, open))
        {
            Cancel($"The sale window changed after submitting {submitted.Name}. Check the retainer list before trying again.");
            return;
        }

        var listed = bridge.ReadExistingListings(out var readError);
        if (readError.Length == 0 && !sellWindowOpen && listed.Any(row => !preListingSlots.Contains(row.Slot)
                && row.ItemId == submitted.ItemId && row.IsHq == submitted.IsHq
                && row.Quantity == submitted.Quantity && row.CurrentPrice == listingSubmittedPrice))
        {
            work = Work.Idle;
            workingItem = null;
            autoApply = false;
            preListingSlots.Clear();
            ResetRequest();
            Status = $"Listed {submitted.Name} at {listingSubmittedPrice:N0} gil each.";
            return;
        }

        if (now > deadline)
        {
            var detail = sellWindowOpen
                ? "The sale window is still open; the plugin did not submit a second confirmation. Check the dialog before continuing."
                : "The retainer list did not show the new listing. Check the retainer before trying again to avoid a duplicate.";
            Cancel($"Could not verify the listing for {submitted.Name}. {detail}");
        }
    }

    private void TickScan(DateTimeOffset now)
    {
        if (index >= Rows.Count)
        {
            var pendingPriceDropCount = Rows.Count(row => row.AwaitingPriceDropDecision);
            if (pendingPriceDropCount > 0)
            {
                Status = $"Review {pendingPriceDropCount} unusually large price drop(s) before continuing.";
                return;
            }
            var updated = Rows.Count(row => row.Status == "Updated");
            var unchanged = Rows.Count(row => row.Status == "Already priced");
            var skipped = Rows.Count - updated - unchanged;
            if (work == Work.AutoUpdateAllRetainers)
            {
                autoUpdated += updated;
                autoAlreadyPriced += unchanged;
                autoSkipped += skipped;
                autoRetainersCompleted++;
                var name = autoRetainers[autoRetainerIndex].Name;
                Status = $"Auto update · {name} complete: {updated} repriced, {unchanged} already at target, {skipped} left unchanged.";
                step = Step.AutoCloseSellList;
                deadline = now.AddSeconds(10);
                return;
            }
            Finish($"Existing listing update complete: {updated} updated, {unchanged} already at target, {skipped} left unchanged because a safe price was unavailable.");
            return;
        }
        var row = Rows[index];
        if (step == Step.Start)
        {
            if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var currentExcludedItemIds, out var currentSavedGearsetItemIds, out var protectionError))
            { Cancel(protectionError); return; }
            if (currentExcludedItemIds.Contains(row.Item.ItemId))
            {
                row.Status = ItemProtection.Reason(row.Item.ItemId, config, currentSavedGearsetItemIds);
                AdvanceScanIndex(row);
                ResetRequest();
                return;
            }
            ResetRequest();
            if (!bridge.TryOpenExisting(row.Item, out var openError)) { ScanFailed(row, openError); return; }
            deadline = now.AddSeconds(8);
            step = Step.Opening;
            return;
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
            if (workingItem is not null)
            {
                if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var currentExcludedItemIds, out var currentSavedGearsetItemIds, out var protectionError))
                { Cancel(protectionError); return; }
                if (currentExcludedItemIds.Contains(workingItem.ItemId))
                {
                    if (!bridge.TryClosePriceWindows(workingItem, out var excludedCloseError))
                    { Cancel($"Could not close the price window for {row.Item.Name}: {excludedCloseError}"); return; }
                    row.Status = ItemProtection.Reason(workingItem.ItemId, config, currentSavedGearsetItemIds);
                    AdvanceScanIndex(row);
                    step = Step.Start;
                    workingItem = null;
                    ResetRequest();
                    return;
                }
            }
            if (workingItem is not null && !row.AwaitingPriceDropDecision && row.Proposal is { CanApply: true } proposal
                && proposal.SuggestedPrice != workingItem.CurrentPrice)
            {
                if (workSource == PriceSource.Local && !bridge.TryCloseCompare(workingItem, out var closeError))
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
            AdvanceScanIndex(row);
            step = Step.Start;
            workingItem = null;
            ResetRequest();
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
                AdvanceScanIndex(row);
                step = Step.Start;
                workingItem = null;
                ResetRequest();
                nextTick = now;
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
            if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var currentExcludedItemIds, out var currentSavedGearsetItemIds, out var protectionError))
            { Cancel(protectionError); return; }
            if (!marketableItemIds.Contains(candidate.ItemId) || currentExcludedItemIds.Contains(candidate.ItemId))
            {
                var reason = currentExcludedItemIds.Contains(candidate.ItemId)
                    ? ItemProtection.Reason(candidate.ItemId, config, currentSavedGearsetItemIds)
                    : "The item is not marketable.";
                SkipBatchItem($"Skipped {candidate.Name}: {reason}");
                return;
            }

            // Listing one stack can leave inventory slots compacted or otherwise shifted by the game.
            // Resolve the next planned item against current inventory before opening it, while still
            // validating the exact item/quality/quantity again in the sale window below.
            var currentInventory = bridge.ReadCarriedInventory(marketableItemIds, currentExcludedItemIds,
                out _, out _, out var inventoryRefreshError);
            if (inventoryRefreshError.Length != 0)
            { Cancel($"Automatic listing stopped because carried inventory could not be refreshed: {inventoryRefreshError}"); return; }
            var currentCandidate = currentInventory.FirstOrDefault(item =>
                item.ItemId == candidate.ItemId && item.IsHq == candidate.IsHq);
            if (currentCandidate is null)
            { SkipBatchItem($"Skipped {candidate.Name}: no matching stack remains in carried inventory."); return; }
            candidate = currentCandidate;
            batchCandidates[index] = candidate;

            var maxTotal = batchSellingOnly ? config.BatchSaleMaxQuantities.GetValueOrDefault(candidate.ItemId) : 0;
            var alreadySold = batchSoldQuantitiesByItemId.GetValueOrDefault(candidate.ItemId);
            if (maxTotal > 0 && alreadySold >= maxTotal)
            {
                if (batchMaximumReachedItemIds.Add(candidate.ItemId))
                    Status = $"Reached the {maxTotal:N0}-item total for {candidate.Name}; leaving remaining inventory untouched.";
                index++;
                return;
            }

            listingCandidate = candidate;
            var perListing = config.BatchSaleQuantities.TryGetValue(candidate.ItemId, out var batchSize)
                ? Math.Min(batchSize, candidate.Quantity)
                : candidate.Quantity;
            var remainingTotal = maxTotal > 0 ? maxTotal - alreadySold : candidate.Quantity;
            requestedListingQuantity = Math.Min(Math.Min(perListing, remainingTotal), 99);
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
            // The game pre-fills its sale dialog at the per-listing cap for stacks larger than 99.
            // Compare against that expected dialog quantity while retaining the full snapshot stack
            // quantity for the remaining-inventory calculation after the listing is confirmed.
            if (listingCandidate is not { } expected || opened.IsExisting || opened.ItemId != expected.ItemId ||
                opened.IsHq != expected.IsHq || opened.Quantity != Math.Min(expected.Quantity, 99) ||
                opened.InventoryType != expected.InventoryType || opened.Slot != expected.Slot)
            { Cancel("Automatic listing stopped because the opened item differs from the inventory snapshot. No price was submitted."); return; }

            if (requestedListingQuantity < opened.Quantity &&
                !bridge.TrySetNewListingQuantity(opened, requestedListingQuantity, out opened, out var quantityError))
            { Cancel($"Automatic listing stopped before pricing {opened.Name}: {quantityError}"); return; }

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
            Status = $"Checking the Universalis price for {opened.Name}{(opened.IsHq ? " (HQ)" : " (NQ)")}...";
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
            if (!ReadRequest(current, now, out var snapshot, out var requestError))
            {
                Status = $"Waiting for the Universalis price for {current.Name}{(current.IsHq ? " (HQ)" : " (NQ)")}...";
                return;
            }
            if (snapshot is null)
            { SkipBatchItem($"Skipped {current.Name}: {requestError}"); return; }

            var proposal = Calculate(snapshot, current);
            if (!proposal.CanApply)
            { SkipBatchItem($"Skipped {current.Name}: {proposal.Error}"); return; }
            if (workSource == PriceSource.Local && !bridge.TryCloseCompare(current, out var closeError))
            { Cancel($"Automatic listing stopped before confirming {current.Name}: {closeError}"); return; }

            listingSubmittedPrice = proposal.SuggestedPrice;
            deadline = now.AddSeconds(8);
            step = Step.ClosingListingCompare;
            Status = workSource == PriceSource.Local
                ? $"Got a price for {current.Name}. Closing the comparison before confirming the sale."
                : $"Got a Universalis price for {current.Name}. Confirming the sale.";
            return;
        }

        if (step == Step.ClosingListingCompare)
        {
            if (workSource == PriceSource.Local && (bridge.IsComparisonVisible || bridge.IsLocalSearchBusy))
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
            if (bridge.IsComparisonVisible || (workSource == PriceSource.Local && bridge.IsLocalSearchBusy))
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
            CompleteSkippedBatchItem();
            return;
        }

        if (step == Step.ClosingSkippedSell)
        {
            if (bridge.IsSellWindowVisible)
            {
                if (now > deadline) Cancel("Automatic listing stopped because the skipped item's sale window did not close. Close it manually before retrying.");
                return;
            }
            CompleteSkippedBatchItem();
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
                if (batchSellingOnly && config.BatchSaleMaxQuantities.TryGetValue(expected.ItemId, out var itemMaximum) && itemMaximum > 0)
                    batchSoldQuantitiesByItemId[expected.ItemId] = batchSoldQuantitiesByItemId.GetValueOrDefault(expected.ItemId) + submitted.Quantity;
                var remaining = expected.Quantity - submitted.Quantity;
                if (remaining > 0)
                {
                    if (!ItemProtection.TryGetExcludedItemIds(bridge, config, out var currentExcludedItemIds, out _, out var protectionError))
                    { Cancel($"The listing was confirmed, but {protectionError}"); return; }
                    var currentInventory = bridge.ReadCarriedInventory(marketableItemIds, currentExcludedItemIds,
                        out _, out _, out var inventoryError);
                    var remainingStack = inventoryError.Length == 0
                        ? currentInventory.FirstOrDefault(item => item.ItemId == expected.ItemId && item.IsHq == expected.IsHq)
                        : null;
                    if (remainingStack is null)
                    {
                        if (now > deadline)
                        { Cancel($"The listing was confirmed, but the remaining {submitted.Name} quantity could not be verified. Check the retainer and inventory before restarting."); return; }
                        Status = $"{submitted.Name} was listed. Waiting for its remaining inventory stack to update before splitting it again...";
                        return;
                    }
                    batchCandidates[index] = remainingStack;
                    listingSucceeded++;
                    listingCandidate = null;
                    workingItem = null;
                    step = Step.Start;
                    ResetRequest();
                    Status = $"Listed {submitted.Name} × {submitted.Quantity:N0}. Continuing with the {remainingStack.Quantity:N0} item(s) remaining in carried inventory.";
                    return;
                }

                listingSucceeded++;
                index++;
                listingCandidate = null;
                workingItem = null;
                step = Step.Start;
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
        CompleteSkippedBatchItem();
    }

    private void CompleteSkippedBatchItem()
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
    }

    private void FinishBatchListing(string message)
    {
        work = Work.Idle;
        batchSellingOnly = false;
        workingItem = null;
        listingCandidate = null;
        batchCandidates.Clear();
        batchSoldQuantitiesByItemId.Clear();
        batchMaximumReachedItemIds.Clear();
        Status = message;
        ResetRequest();
    }

    private void ScanReceived(PriceRow row, PriceSnapshot snapshot)
    {
        row.Snapshot = snapshot;
        row.Proposal = Calculate(snapshot, row.Item);
        row.PriceDropReviewThresholdPercent = config.PriceDropReviewPercentFor(row.Item.CurrentPrice);
        if (!row.Proposal.CanApply) row.Status = row.Proposal.Error!;
        else if (row.Proposal.SuggestedPrice == row.Item.CurrentPrice) row.Status = "Already priced";
        else if (!row.PriceDropApproved && (decimal)row.Proposal.SuggestedPrice * 100
            < (decimal)row.Item.CurrentPrice * (100 - row.PriceDropReviewThresholdPercent))
        {
            row.AwaitingPriceDropDecision = true;
            row.Status = $"Held for review: target is more than {row.PriceDropReviewThresholdPercent}% below the current price.";
        }
        else row.Status = "Ready";
        step = Step.Closing;
    }

    private void AdvanceScanIndex(PriceRow row)
        => index = row.PriceDropApproved ? Rows.Count : index + 1;

    private void ScanFailed(PriceRow row, string error)
    {
        row.Status = error;
        if (workingItem is { DialogGeneration: > 0 }) step = Step.Closing;
        else { AdvanceScanIndex(row); step = Step.Start; }
    }

    private bool StartRequest(SellItem item, DateTimeOffset now, out string error)
    {
        error = "";
        deadline = now.AddSeconds(25);
        if (workSource == PriceSource.Universalis)
        {
            if ((config.UseDataCenterPrices || config.UseRegionPrices) && string.IsNullOrWhiteSpace(item.Session.DataCenterName))
            { error = "Could not identify your home world's Data Center. No price lookup was started."; return false; }
            quoteTask = universalis.FetchAsync(item.Session.WorldId, item.ItemId,
                cancellation.Token, config.UniversalisCacheMinutes,
                config.UseDataCenterPrices || config.UseRegionPrices ? item.Session.DataCenterName : null,
                config.UseRegionPrices);
        }
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
        error = workSource == PriceSource.Universalis
            ? "Universalis price check timed out. Try again later."
            : "Local price check timed out. Try again.";
        cancellation.Cancel();
        return true;
    }

    private PriceProposal Calculate(PriceSnapshot snapshot, SellItem item)
        => Calculate(snapshot, item.ItemId, item.Session.WorldId, item.IsHq, bridge.OwnRetainerIds(),
            snapshot.Source == PriceSource.Universalis && (config.UseDataCenterPrices || config.UseRegionPrices)
                ? item.Session.DataCenterName : null);

    private PriceProposal Calculate(PriceSnapshot snapshot, uint itemId, uint worldId, bool isHq,
        IReadOnlySet<ulong> ownRetainerIds, string? dataCenterName = null)
        => PriceCalculator.Calculate(snapshot, itemId, worldId, isHq, ownRetainerIds, (uint)config.MinimumPrice,
            DateTimeOffset.UtcNow, config.UseMaximumPriceAge ? TimeSpan.FromMinutes(config.MaximumAgeMinutes) : null,
            dataCenterName);

    private bool IsNoReprice(uint itemId) => config.NoRepriceItemIds.Contains(itemId);

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
        var wasSingleListing = work == Work.Single && autoApply;
        var wasAutoUpdating = work == Work.AutoUpdateAllRetainers;
        ResetRequest();
        work = Work.Idle;
        manualTarget = null;
        if (wasAutoUpdating)
        {
            autoRetainers.Clear();
            autoRetainerIndex = 0;
            session = null;
        }
        if (wasBatchListing)
        {
            // A batch can stop while its item dialog is still on screen. Don't let the
            // idle one-item auto-pricer take that same dialog over and hide the stop reason.
            suppressAutoPriceUntilSellWindowCloses = bridge.IsSellWindowVisible;
            batchCandidates.Clear();
            batchSellingOnly = false;
            listingCandidate = null;
            workingItem = null;
        }
        else if (wasSingleListing)
        {
            workingItem = null;
            autoApply = false;
            preListingSlots.Clear();
        }
        Status = message;
    }

    private static bool SameListingIdentity(SellItem a, SellItem b) => a.Session == b.Session && a.ItemId == b.ItemId
        && a.IsHq == b.IsHq && a.Quantity == b.Quantity && a.InventoryType == b.InventoryType && a.Slot == b.Slot;
    private static bool SameListing(SellItem a, SellItem b) => SameListingIdentity(a, b) && a.CurrentPrice == b.CurrentPrice;
    private static bool SameDialog(SellItem a, SellItem b) => SameListing(a, b) && a.DialogGeneration == b.DialogGeneration;

    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
}
