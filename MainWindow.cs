using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerPricer;

internal sealed class MainWindow : Window
{
    private const string EmptyBatchListPopup = "Batch selling list is empty.";
    private const string PriceDropReviewPopup = "Review large price drop";
    private readonly PluginConfig config;
    private readonly PricingController controller;
    private readonly IReadOnlyList<ItemChoice> itemChoices;
    private readonly Func<MarketWorld?> homeWorld;
    private readonly Action save;
    private readonly Action<Action> dispatch;
    private readonly Func<string?> retainerError;
    private readonly FeedbackClient feedback;
    private readonly SniperMonitor sniper;
    private readonly AutoVendorController vendor;
    private string lookupSearch = "";
    private string lookupSearchCache = "";
    private List<ItemChoice> lookupMatches = [];
    private ItemChoice? lookupSelection;
    private bool lookupHq;
    private string exceptionSearch = "";
    private string exceptionSearchCache = "";
    private List<ItemChoice> exceptionMatches = [];
    private ItemChoice? exceptionSelection;
    private string? exceptionBulkAddMessage;
    private string noRepriceSearch = "";
    private string noRepriceSearchCache = "";
    private List<ItemChoice> noRepriceMatches = [];
    private ItemChoice? noRepriceSelection;
    private string batchSearch = "";
    private string batchSearchCache = "";
    private List<ItemChoice> batchMatches = [];
    private ItemChoice? batchSelection;
    private int batchQuantityInput = 10;
    private int batchMaximumTotalInput;
    private string? batchBulkAddMessage;
    private string feedbackMessage = "";
    private string? feedbackStatus;
    private bool feedbackSending;

    public MainWindow(PluginConfig config, PricingController controller, IReadOnlyList<ItemChoice> itemChoices,
        Func<MarketWorld?> homeWorld, Action save, Action<Action> dispatch,
        Func<string?> retainerError, FeedbackClient feedback, SniperMonitor sniper, AutoVendorController vendor) : base("Retainer Pricer")
    {
        (this.config, this.controller, this.itemChoices, this.homeWorld, this.save, this.dispatch, this.retainerError) =
            (config, controller, itemChoices, homeWorld, save, dispatch, retainerError);
        this.feedback = feedback;
        this.sniper = sniper;
        this.vendor = vendor;
        if (config.Source != PriceSource.Universalis)
        {
            config.Source = PriceSource.Universalis;
            save();
        }
        Size = new Vector2(860, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var world = homeWorld();
        var priceScope = config.UseRegionPrices
            ? "your home-world region and Materia (Oceania)"
            : config.UseDataCenterPrices
            ? world?.DataCenterName is { Length: > 0 } dcName ? $"the {dcName} data center" : "your home-world data center"
            : "your home world";
        ImGui.TextWrapped($"Universalis prices use {priceScope}. HQ and NQ are compared separately; your own retainers are excluded.");
        if (retainerError() is { } nativeError) ImGui.TextWrapped(nativeError);
        var busy = controller.Busy || vendor.IsRunning;
        ImGui.BeginDisabled(busy);
        if (controller.IsAutoUpdatingAllRetainers)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.48f, 0.2f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.58f, 0.25f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.09f, 0.4f, 0.16f, 1));
            ImGui.Button("Auto update...");
            ImGui.PopStyleColor(3);
        }
        else if (ImGui.Button("Auto update")) dispatch(controller.AutoUpdateAllRetainers);
        ImGui.SameLine();
        if (controller.IsListingItemsRunning && !controller.IsBatchSellingOnlyRunning)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.48f, 0.2f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.58f, 0.25f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.09f, 0.4f, 0.16f, 1));
            ImGui.Button("Listing items...");
            ImGui.PopStyleColor(3);
        }
        else if (ImGui.Button("Start listing items")) dispatch(controller.StartListingItems);
        ImGui.SameLine();
        if (controller.IsUpdatingListings && !controller.IsAutoUpdatingAllRetainers)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.48f, 0.2f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.58f, 0.25f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.09f, 0.4f, 0.16f, 1));
            ImGui.Button("Updating listings...");
            ImGui.PopStyleColor(3);
        }
        else if (ImGui.Button("Update existing listings")) dispatch(controller.UpdateExistingListings);
        ImGui.SameLine();
        if (controller.IsBatchSellingOnlyRunning)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.48f, 0.2f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.58f, 0.25f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.09f, 0.4f, 0.16f, 1));
            ImGui.Button("Batch selling only...");
            ImGui.PopStyleColor(3);
        }
        else if (ImGui.Button("Start batch selling only"))
        {
            if (config.BatchSaleQuantities.Count == 0) ImGui.OpenPopup(EmptyBatchListPopup);
            else dispatch(controller.StartBatchSellingOnly);
        }
        ImGui.EndDisabled();
        if (ImGui.BeginPopupModal(EmptyBatchListPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(EmptyBatchListPopup);
            if (ImGui.Button("OK")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (busy)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.68f, 0.12f, 0.12f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.18f, 0.18f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.55f, 0.08f, 0.08f, 1));
            if (ImGui.Button("Stop")) dispatch(() =>
            {
                if (vendor.IsRunning) vendor.Cancel();
                else controller.Cancel();
            });
            ImGui.PopStyleColor(3);
        }
        ImGui.TextDisabled("Auto update can start at the retainer picker and go top-to-bottom, or start with the open selling list. Start listing items processes eligible carried inventory; batch-only processes the Batch selling tab. Update existing listings handles only the open retainer.");
        if (controller.Busy)
            ImGui.TextDisabled("The active listing task is highlighted. Stop cancels it; changes already submitted remain applied.");
        if (controller.StartListingAvailabilityError is { } pricingError) ImGui.TextWrapped(pricingError);
        ImGui.BeginDisabled(controller.Busy || sniper.IsRunning || vendor.IsRunning);
        var automatic = config.AutoPriceNewListings;
        if (ImGui.Checkbox("Automatically price and confirm new listings", ref automatic)) { config.AutoPriceNewListings = automatic; save(); }
        ImGui.TextDisabled("This checkbox auto-prices only the currently opened sale item. Use Start listing items to process all eligible inventory and continue through the list automatically.");
        var source = (int)PriceSource.Universalis;
        ImGui.SetNextItemWidth(250);
        ImGui.Combo("On-screen price check source", ref source, "Universalis\0");
        ImGui.TextWrapped(config.UseMaximumPriceAge
            ? $"Check price again uses Universalis. Upload data older than {config.MaximumAgeMinutes} minutes is skipped. Automatic listing actions also require a sale in the last 20 days."
            : "Check price again uses Universalis. Upload age is not filtered; automatic listing actions also require sales in the last 20 days.");
        ImGui.EndDisabled();
        ImGui.Separator();

        ImGui.TextWrapped(vendor.IsRunning ? vendor.Status : controller.Status);
        if (controller.Busy)
        {
            if (!string.IsNullOrEmpty(controller.Progress)) ImGui.TextUnformatted(controller.Progress);
        }
        DrawPriceDropReviewPopup();
        if (ImGui.BeginTabBar("##pricingTabs"))
        {
            if (ImGui.BeginTabItem("New / selected item")) { DrawCurrent(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Price lookup")) { DrawManualLookup(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Existing listings")) { DrawExisting(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Exceptions")) { DrawExceptions(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Don't reprice")) { DrawNoReprice(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Batch selling")) { DrawBatchSelling(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Auto vendor")) { DrawAutoVendor(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Settings")) { DrawSettings(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("?")) { DrawHelp(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Sniper")) { DrawSniper(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        var version = typeof(MainWindow).Assembly.GetName().Version;
        var versionLabel = version is null ? "Version unavailable" : $"v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        ImGui.SetCursorPos(new Vector2(ImGui.GetStyle().WindowPadding.X,
            ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetTextLineHeight()));
        ImGui.TextDisabled(versionLabel);
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
        var protectedExisting = item.IsExisting && config.NoRepriceItemIds.Contains(item.ItemId);
        if (protectedExisting)
            ImGui.TextDisabled("This existing listing is protected by Don't reprice. Remove it from that list before changing its price.");
        ImGui.BeginDisabled(controller.Busy || protectedExisting);
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
                    ImGui.BeginDisabled(controller.Busy || protectedExisting);
                    if (ImGui.Button("Apply price to selling window")) dispatch(controller.FillCurrent);
                    ImGui.EndDisabled();
                }
                else ImGui.TextWrapped(quote.Error ?? "No usable price.");
            }
        }
    }

    private void DrawExisting()
    {
        ImGui.TextWrapped("The Update existing listings button checks eligible items against Universalis, undercuts comparable listings by one gil, and verifies the retainer accepted each change. Excluded and Don't reprice items are skipped. Large price drops use the configurable review bands below for both Update existing listings and Auto update.");
        if (controller.ExistingUpdateError is { } updateError) ImGui.TextWrapped(updateError);
        if (controller.ExistingApplyError is { } applyError) ImGui.TextWrapped(applyError);
        ImGui.TextUnformatted("Large price-drop review bands");
        ImGui.TextDisabled("A proposal is held when it falls by more than the selected percentage from the current listing price.");
        ImGui.BeginDisabled(controller.Busy || sniper.IsRunning || vendor.IsRunning);
        var under10K = config.PriceDropUnder10KPercent;
        ImGui.SetNextItemWidth(145);
        if (ImGui.InputInt("1–9,999 gil (%)", ref under10K))
        { config.PriceDropUnder10KPercent = under10K; config.Normalize(); save(); }
        ImGui.SameLine();
        var from10KTo999K = config.PriceDrop10KTo999KPercent;
        ImGui.SetNextItemWidth(175);
        if (ImGui.InputInt("10,000–999,999 gil (%)", ref from10KTo999K))
        { config.PriceDrop10KTo999KPercent = from10KTo999K; config.Normalize(); save(); }
        ImGui.NewLine();
        var from1MTo9999K = config.PriceDrop1MTo9999KPercent;
        ImGui.SetNextItemWidth(175);
        if (ImGui.InputInt("1m–9,999,999 gil (%)", ref from1MTo9999K))
        { config.PriceDrop1MTo9999KPercent = from1MTo9999K; config.Normalize(); save(); }
        ImGui.SameLine();
        var from10M = config.PriceDrop10MPlusPercent;
        ImGui.SetNextItemWidth(135);
        if (ImGui.InputInt("10m+ gil (%)", ref from10M))
        { config.PriceDrop10MPlusPercent = from10M; config.Normalize(); save(); }
        ImGui.EndDisabled();
        ImGui.Separator();
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
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 132);
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
                ImGui.SameLine();
                if (ImGui.SmallButton("Protect")) AddNoReprice(row.Item.ItemId);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private void DrawPriceDropReviewPopup()
    {
        var row = controller.PriceDropReviewItem;
        if (row is null) return;

        ImGui.OpenPopup(PriceDropReviewPopup);
        if (!ImGui.BeginPopupModal(PriceDropReviewPopup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped($"The proposed price is more than {row.PriceDropReviewThresholdPercent}% below this listing's current price. It was held while the rest of this retainer was processed.");
        ImGui.Separator();
        ImGui.TextUnformatted($"{row.Item.Name}{(row.Item.IsHq ? " (HQ)" : " (NQ)")}");
        ImGui.TextUnformatted($"Current: {row.Item.CurrentPrice:N0} gil each");
        ImGui.TextUnformatted($"Proposed: {row.Proposal?.SuggestedPrice:N0} gil each");
        ImGui.TextDisabled("The percentage comes from the current listing price's band in Existing listings. Approving retrieves the price again and applies only the fresh result. Auto update continues to the next retainer after all held items are reviewed.");
        if (ImGui.Button("Still undercut regardless"))
        {
            ImGui.CloseCurrentPopup();
            dispatch(controller.ApprovePriceDrop);
        }
        ImGui.SameLine();
        if (ImGui.Button("Ignore"))
        {
            ImGui.CloseCurrentPopup();
            dispatch(controller.IgnorePriceDrop);
        }
        ImGui.EndPopup();
    }

    private void DrawSettings()
    {
        ImGui.BeginDisabled(controller.Busy || sniper.IsRunning || vendor.IsRunning);
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
        var cacheMinutes = config.UniversalisCacheMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Reuse Universalis cache (minutes; 0 = off)", ref cacheMinutes))
        { config.UniversalisCacheMinutes = cacheMinutes; config.Normalize(); save(); }
        ImGui.TextDisabled(config.UniversalisCacheMinutes == 0
            ? "Always fetch fresh Universalis data."
            : $"Reuse successful Universalis responses for up to {config.UniversalisCacheMinutes} minutes in this session, for the same item and market scope.");
        var useDataCenter = config.UseDataCenterPrices;
        if (ImGui.Checkbox("Use lowest price in the Data Center", ref useDataCenter))
        { config.UseDataCenterPrices = useDataCenter; if (useDataCenter) config.UseRegionPrices = false; save(); }
        ImGui.TextDisabled(config.UseDataCenterPrices
            ? "Universalis checks listings across your home world's Data Center for automatic pricing and manual lookups. Local marketboard checks always stay on your home world."
            : "Off by default. Turn this on to include listings across your home world's Data Center.");
        var useRegion = config.UseRegionPrices;
        if (ImGui.Checkbox("Use lowest price in region", ref useRegion))
        { config.UseRegionPrices = useRegion; if (useRegion) config.UseDataCenterPrices = false; save(); }
        ImGui.TextDisabled(config.UseRegionPrices
            ? "Universalis checks all Data Centers in your home-world region plus Materia (Oceania). This also applies to automatic pricing and manual lookups; it is mutually exclusive with Data Center pricing."
            : "Off by default. Turn this on to include every Data Center in your home-world region and Materia (Oceania).");
        var open = config.OpenWithRetainer;
        if (ImGui.Checkbox("Open this window with the retainer selling list", ref open)) { config.OpenWithRetainer = open; save(); }
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.TextWrapped("Prices are per item, before tax. If an undercut would be below your minimum, or no matching competitor is available, that item is left unchanged.");
        ImGui.TextWrapped("Changing character, world, or active retainer stops a batch. Each price submission rechecks the exact item window first. Stop prevents further submissions; completed price changes stay applied.");
    }

    private void DrawHelp()
    {
        if (!ImGui.BeginChild("##helpContents", new Vector2(0, Math.Max(120, ImGui.GetContentRegionAvail().Y)), true))
        {
            ImGui.EndChild();
            return;
        }
        ImGui.TextWrapped("Quick guide: set your exclusions and optional batch sizes once, open a retainer's selling list, then choose the action you need.");
        ImGui.TextDisabled("Open this window with /retainerpricer or /retainer.");
        ImGui.Separator();

        ImGui.TextUnformatted("Top buttons");
        ImGui.BulletText("Auto update: start from the retainer picker to visit retainers top-to-bottom, or start from any retainer's selling list to process that one first. It advances the greeting dialogue, retrying while the Talk box remains open, and skips unavailable retainers. Stop halts the run; already submitted changes remain applied.");
        ImGui.BulletText("Start listing items: checks eligible items in your carried inventory and lists them one by one. No sale exceeds 99 items; larger stacks continue in follow-up listings. Exceptions, items in saved gear sets, bound items, untradeable items, and items the market does not support are skipped. The run stops when it finishes or the retainer's 20 listing slots are full.");
        ImGui.BulletText("Update existing listings: reprices eligible listings on the currently open retainer. A proposed price drop above the configurable percentage for its current-price band is held for review at the end of that retainer; approve it to recheck and apply, or ignore it. Set the four bands in the Existing listings tab. Auto update uses the same bands and pauses at the same review before moving to the next retainer.");
        ImGui.BulletText("Start batch selling only: lists only the items in the Batch selling tab. It ignores other inventory, respects each item's per-listing size and optional per-run total, and caps each sale at 99 items before continuing the remainder.");
        ImGui.BulletText("Stop: stops further actions in the current run. Any price changes already submitted remain in place.");

        ImGui.Separator();
        ImGui.TextUnformatted("Tabs");
        ImGui.BulletText("New / selected item: shows the item sale window currently open in game. Check price again gets a suggestion; Apply price to selling window fills the price without confirming the sale. The automatic new-item option can price and confirm newly opened eligible sale windows.");
        ImGui.BulletText("Price lookup: search an item and retrieve its price without opening a retainer sale window. This is read-only and never changes a listing. In Captured items, Snapshot inventory or Snapshot retainer listings fills a list with Retrieve, List, and Exclude actions.");
        ImGui.BulletText("Existing listings: set the four configurable price-drop review percentages for listings from 1–9,999 gil, 10,000–999,999 gil, 1,000,000–9,999,999 gil, and 10,000,000 gil or more. Both Update existing listings and Auto update use the percentage band selected by the listing's current price. Review scan results below and use Exclude beside an item to add it to your exception list.");
        ImGui.BulletText("Exceptions: items here are skipped by automatic listing and repricing. Items assigned to saved gear sets are automatically protected too; they do not need to be added here. Refresh carried inventory to find items, filter by name, add a selected item, or add the current marketable inventory at once. Remove an exception to allow that item again unless a saved gear set still uses it.");
        ImGui.BulletText("Don't reprice: block price changes to existing listings through Update existing listings, Auto update, or the current-item price controls. Read-only lookups still work. These items can still be listed from your carried inventory; use Exceptions to skip both listing and repricing.");
        ImGui.BulletText("Batch selling: choose items and set the maximum quantity in each listing. The optional total limit caps how much of that item is listed in one batch-only run; 0 means no total cap. Add current inventory adds marketable carried items using the current size and limit.");
        ImGui.BulletText("Auto vendor: open an NPC vendor Shop window, set the price threshold, and start vendoring. Eligible carried stacks with a complete Universalis listing at or below the threshold are sold to the vendor; Exceptions, saved gear-set items, bound items, unmarketable items, missing prices, and your own retainer listings are skipped. The game’s vendor sale action sells the checked stack directly, then the plugin verifies the inventory change before continuing. Vendor sales cannot be undone.");
        ImGui.BulletText("Sniper: Start watching scans all marketable items in the selected world, Data Center, or region scope in batches of up to 100, spacing history queries at least one second apart, then listens for new listings across the same scope. Set the sale-history window, deal threshold as a percentage of the median (91% by default), minimum sales, and minimum listing value. Ordinary deals below the minimum value are hidden; 1-gil alerts always show. Click the Server header to group by server and the Listing header to sort prices high-to-low or low-to-high. Purchases are manual.");
        ImGui.BulletText("Settings: set the minimum price, optionally reject old price data, choose how long successful Universalis results are reused, and optionally compare across your Data Center or region. Data Center and region options cannot be used together.");

        ImGui.Separator();
        ImGui.TextUnformatted("How prices work");
        ImGui.BulletText("Prices come from Universalis. HQ and normal-quality items are checked separately. The plugin ignores your own retainers and aims for one gil below the lowest matching competitor, while respecting your minimum price.");
        ImGui.BulletText("Automatic listing and repricing require a current competing listing and a sale within the last 20 days. With the optional age filter off, old price uploads are still allowed; the recent-sale rule remains.");
        ImGui.BulletText("Prices shown are per item and before tax. If there is no safe price, the plugin leaves that item unchanged.");
        ImGui.BulletText("Use this on your home world with a retainer's selling list open. Switching character, world, or active retainer, disconnecting, or entering a loading state stops the active run.");

        ImGui.Separator();
        ImGui.TextUnformatted("Send feedback");
        ImGui.TextWrapped("Describe what happened, what you expected, and which button or tab you used. Please do not include passwords or account details.");
        ImGui.InputTextMultiline("##feedbackMessage", ref feedbackMessage, 4001, new Vector2(0, 120));
        ImGui.TextDisabled($"{feedbackMessage.Length}/4000 characters · your note and plugin version are emailed to the developer; no character or market data is attached.");
        ImGui.BeginDisabled(!feedback.IsConfigured || feedbackSending || string.IsNullOrWhiteSpace(feedbackMessage) || feedbackMessage.Trim().Length > 4000);
        if (ImGui.Button(feedbackSending ? "Sending feedback..." : "Send feedback")) StartFeedbackSend();
        ImGui.EndDisabled();
        if (!feedback.IsConfigured)
            ImGui.SameLine();
        if (!feedback.IsConfigured)
            ImGui.TextDisabled("Feedback email setup is not finished yet.");
        if (feedbackStatus is { Length: > 0 } status) ImGui.TextWrapped(status);
        ImGui.EndChild();
    }

    private void DrawSniper()
    {
        var world = homeWorld();
        var marketScope = config.UseRegionPrices
            ? "your home-world region plus Oceania (Materia)"
            : config.UseDataCenterPrices
                ? world?.DataCenterName is { Length: > 0 } dataCenterName
                    ? $"the {dataCenterName} Data Center"
                    : "your home-world Data Center"
                : "your home world";
        ImGui.TextWrapped($"Start watching to scan every marketable item in {marketScope}, then listen for new listings across the same scope. Sniper highlights deals and 1-gil listings for you to review; it never buys automatically.");
        ImGui.TextDisabled("History is requested in batches of up to 100 items, with at least 1 second between batch requests. The all-item scan can take a little while; Stop watching cancels it.");

        var isRunning = sniper.IsRunning;
        ImGui.BeginDisabled(isRunning);
        var thresholdPercent = (float)(config.SniperThresholdFraction * 100.0);
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Threshold (% of median)", ref thresholdPercent, 1.0f, 5.0f, "%.1f%%"))
        {
            config.SniperThresholdFraction = Math.Clamp(thresholdPercent / 100.0f, 0.01f, 1.0f);
            config.Normalize();
            save();
        }
        ImGui.SameLine();
        var historyDays = config.SniperHistoryDays;
        ImGui.SetNextItemWidth(130);
        if (ImGui.InputInt("History days (3–14)", ref historyDays))
        {
            config.SniperHistoryDays = historyDays;
            config.Normalize();
            save();
        }
        ImGui.SameLine();
        var minimumSales = config.SniperMinimumSales14Days;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Minimum sales in window", ref minimumSales))
        {
            config.SniperMinimumSales14Days = minimumSales;
            config.Normalize();
            save();
        }
        ImGui.SetNextItemWidth(150);
        var minimumItemPrice = config.SniperMinimumItemPrice;
        if (ImGui.InputInt("Minimum item value (gil each)", ref minimumItemPrice))
        {
            config.SniperMinimumItemPrice = minimumItemPrice;
            config.Normalize();
            save();
        }
        ImGui.TextDisabled("Ordinary deals below this price are hidden. 1-gil alerts always show.");

        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted($"Search scope · all {sniper.MarketableItemCount:N0} marketable items · {marketScope}");
        ImGui.TextDisabled("Start watching fetches the configured sales window in 100-item calls, builds separate HQ/NQ medians, then opens the live listing feed.");

        ImGui.Separator();
        ImGui.BeginDisabled(isRunning || world is null);
        if (ImGui.Button("Start watching")) sniper.Start(world, config.UseDataCenterPrices, config.UseRegionPrices);
        ImGui.EndDisabled();
        if (sniper.IsRunning)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.68f, 0.12f, 0.12f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.18f, 0.18f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.55f, 0.08f, 0.08f, 1));
            if (ImGui.Button("Stop watching")) sniper.Stop();
            ImGui.PopStyleColor(3);
        }
        ImGui.TextWrapped(sniper.Status);
        ImGui.TextDisabled($"Deals: listing ≥ {config.SniperMinimumItemPrice:N0} gil each and ≤ {config.SniperThresholdFraction:P1} of the {config.SniperHistoryDays}-day HQ/NQ median, with at least {config.SniperMinimumSales14Days} sales in that window. Up to 1,800 recent sales are used per item. New 1-gil listings always show; purchases are manual.");

        var deals = sniper.Deals;
        ImGui.Separator();
        ImGui.TextUnformatted($"Deals · {deals.Count}");
        if (deals.Count == 0)
        {
            ImGui.TextDisabled("No qualifying new listings received yet.");
            return;
        }
        if (ImGui.BeginTable("##sniperDeals", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                new Vector2(0, Math.Max(140, ImGui.GetContentRegionAvail().Y - 25))))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Server");
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("Listing", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 82);
            ImGui.TableSetupColumn($"{config.SniperHistoryDays}d median", ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            var sortedDeals = deals.ToList();
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsCount > 0)
            {
                var sortSpec = sortSpecs.Specs[0];
                var ascending = sortSpec.SortDirection == ImGuiSortDirection.Ascending;
                sortedDeals = sortSpec.ColumnIndex switch
                {
                    0 => ascending
                        ? sortedDeals.OrderBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList()
                        : sortedDeals.OrderByDescending(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList(),
                    1 => ascending
                        ? sortedDeals.OrderBy(deal => deal.WorldName, StringComparer.OrdinalIgnoreCase).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList()
                        : sortedDeals.OrderByDescending(deal => deal.WorldName, StringComparer.OrdinalIgnoreCase).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList(),
                    2 => ascending
                        ? sortedDeals.OrderBy(deal => deal.IsHq).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList()
                        : sortedDeals.OrderByDescending(deal => deal.IsHq).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList(),
                    3 => ascending
                        ? sortedDeals.OrderBy(deal => deal.PricePerUnit).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList()
                        : sortedDeals.OrderByDescending(deal => deal.PricePerUnit).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList(),
                    4 => ascending
                        ? sortedDeals.OrderBy(deal => deal.MedianSalePrice).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList()
                        : sortedDeals.OrderByDescending(deal => deal.MedianSalePrice).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList(),
                    5 => ascending
                        ? sortedDeals.OrderBy(deal => deal.SalesInHistoryWindow).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList()
                        : sortedDeals.OrderByDescending(deal => deal.SalesInHistoryWindow).ThenBy(deal => deal.ItemName, StringComparer.OrdinalIgnoreCase).ToList(),
                    6 => ascending
                        ? sortedDeals.OrderBy(deal => deal.DetectedAt).ToList()
                        : sortedDeals.OrderByDescending(deal => deal.DetectedAt).ToList(),
                    _ => sortedDeals
                };
            }
            foreach (var deal in sortedDeals)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (deal.IsOneGilAlert)
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), $"1 GIL ALERT · {deal.ItemName} · qty {deal.Quantity}");
                else
                    ImGui.TextWrapped($"{deal.ItemName} · qty {deal.Quantity}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(deal.WorldName);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(deal.IsHq ? "HQ" : "NQ");
                ImGui.TableNextColumn();
                if (deal.IsOneGilAlert) ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), $"{deal.PricePerUnit:N0}");
                else ImGui.TextUnformatted(deal.PricePerUnit.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(deal.MedianSalePrice > 0 ? deal.MedianSalePrice.ToString("N0") : "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(deal.SalesInHistoryWindow > 0 ? deal.SalesInHistoryWindow.ToString("N0") : "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(deal.DetectedAt.ToLocalTime().ToString("HH:mm:ss"));
            }
            ImGui.EndTable();
        }
    }

    private void DrawAutoVendor()
    {
        var scope = config.UseRegionPrices
            ? "the home-world region plus Oceania (Materia)"
            : config.UseDataCenterPrices
                ? $"the {homeWorld()?.DataCenterName ?? "home-world"} Data Center"
                : "your home world";
        ImGui.TextWrapped("Auto vendor checks carried marketable inventory and sells whole stacks whose matching HQ/NQ market listing is at or below the threshold. It uses the price scope from Settings and always skips Exceptions, items assigned to saved gear sets, and your own retainer listings.");
        ImGui.TextWrapped("Open an NPC vendor's Shop window before starting. Auto vendor checks each stack with Universalis, calls the game's vendor sale action for its verified inventory slot, then confirms the inventory change before continuing. It closes an already-open item menu belonging to this vendor before continuing; bound gear is skipped.");
        ImGui.TextDisabled($"Price scope: {scope}. A missing, incomplete, or failed price check is skipped. Vendor sales cannot be undone; add items to Exceptions before starting if you want to keep them. Saved gear-set items are protected automatically.");

        ImGui.BeginDisabled(controller.Busy || vendor.IsRunning);
        var threshold = config.AutoVendorPriceThreshold;
        ImGui.SetNextItemWidth(170);
        if (ImGui.InputInt("Sell at or below (gil per item)", ref threshold))
        {
            config.AutoVendorPriceThreshold = threshold;
            config.Normalize();
            save();
        }
        ImGui.EndDisabled();

        if (!vendor.IsRunning)
        {
            ImGui.BeginDisabled(controller.Busy);
            if (ImGui.Button("Start vendoring")) dispatch(vendor.Start);
            ImGui.EndDisabled();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.48f, 0.2f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.58f, 0.25f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.09f, 0.4f, 0.16f, 1));
            ImGui.Button("Vendoring…");
            ImGui.PopStyleColor(3);
            ImGui.SameLine();
            ImGui.TextDisabled($"{vendor.Progress} / {vendor.CandidateCount} stacks");
        }
        ImGui.TextWrapped(vendor.Status);
    }

    private void StartFeedbackSend()
    {
        if (feedbackSending || string.IsNullOrWhiteSpace(feedbackMessage)) return;
        var submittedMessage = feedbackMessage.Trim();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var versionLabel = version is null ? "unknown" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        feedbackSending = true;
        feedbackStatus = "Sending your note…";
        _ = SendFeedbackAsync(submittedMessage, versionLabel);
    }

    private async Task SendFeedbackAsync(string message, string version)
    {
        string result;
        var sent = false;
        try
        {
            await feedback.SendAsync(message, version).ConfigureAwait(false);
            result = "Feedback sent. Thank you.";
            sent = true;
        }
        catch (Exception ex)
        {
            result = ex is InvalidOperationException
                ? ex.Message
                : "Feedback could not be sent. Check your connection and try again later.";
        }

        dispatch(() =>
        {
            feedbackSending = false;
            feedbackStatus = result;
            if (sent) feedbackMessage = "";
        });
    }

    private void DrawManualLookup()
    {
        ImGui.TextWrapped("Search any item and retrieve its Universalis price from your selected market scope without opening a retainer sale window. This lookup is read-only; it never changes a listing.");
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
                var resultScope = snapshot.RegionName
                    ?? (config.UseDataCenterPrices ? world?.DataCenterName ?? "the Data Center"
                        : world?.Name ?? "the home world");
                ImGui.TextUnformatted($"Received {snapshot.Listings.Count:N0} listings for {resultScope}.");
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
        ImGui.TextWrapped("Open a retainer's selling list and use Start listing items or Start batch selling only above. The first processes all eligible carried inventory; the second processes only items in the Batch selling tab. Both skip untradeable, nonmarketable, and excluded items and stop when the 20 listing slots are full. Use Stop to halt the batch.");
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
            ImGui.TextDisabled($"Skipped {controller.InventoryExceptionSkipped} excluded stack(s) and {controller.InventoryUnmarketableSkipped} bound, untradeable, or nonmarketable stack(s).");
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
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 260);
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
            ImGui.SameLine();
            if (config.NoRepriceItemIds.Contains(item.ItemId))
                ImGui.TextDisabled("Protected");
            else
            {
                ImGui.BeginDisabled(controller.Busy || config.ExcludedItemIds.Contains(item.ItemId));
                if (ImGui.SmallButton("Don't reprice")) AddNoReprice(item.ItemId);
                ImGui.EndDisabled();
            }
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
        ImGui.TextUnformatted("Exception list");
        ImGui.TextWrapped("Items in Exceptions are always skipped by automatic listing and existing-listing updates. Items assigned to saved gear sets are also protected automatically, including during Auto vendor. Untradeable and nonmarketable items are omitted automatically. Manual price lookups remain available.");

        ImGui.BeginDisabled(controller.Busy);
        if (ImGui.Button(controller.ExceptionInventorySnapshotAt is null ? "Grab carried inventory" : "Refresh carried inventory"))
            dispatch(controller.SnapshotExceptionInventory);
        ImGui.EndDisabled();
        if (controller.ExceptionInventorySnapshotError is { } inventoryError)
            ImGui.TextWrapped(inventoryError);
        else if (controller.ExceptionInventorySnapshotAt is { } snapshotAt)
        {
            ImGui.TextUnformatted($"Inventory snapshot · {controller.ExceptionInventoryCandidates.Count} marketable stack(s) · {snapshotAt:HH:mm:ss}");
            ImGui.TextDisabled($"{controller.ExceptionInventoryUnmarketableSkipped} bound, untradeable, or nonmarketable stack(s) omitted.");
        }
        else
            ImGui.TextDisabled("Grab carried inventory to populate the picker, or search the full item list below.");

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("Filter item names", ref exceptionSearch, 128);
        if (!StringComparer.CurrentCultureIgnoreCase.Equals(exceptionSearch, exceptionSearchCache))
            exceptionSelection = null;
        RefreshMatches(exceptionSearch, ref exceptionSearchCache, ref exceptionMatches);

        var inventoryItems = controller.ExceptionInventoryCandidates
            .GroupBy(item => item.ItemId)
            .Select(group => new ItemChoice(group.Key, group.First().Name))
            .ToList();
        var pickerItems = inventoryItems.Concat(exceptionMatches)
            .Where(item => !config.ExcludedItemIds.Contains(item.ItemId))
            .GroupBy(item => item.ItemId).Select(group => group.First()).ToList();
        var preview = exceptionSelection is { } selected
            ? $"{selected.Name}  ·  #{selected.ItemId}"
            : "Select an inventory or matching item...";
        ImGui.SetNextItemWidth(420);
        if (ImGui.BeginCombo("Item to exclude", preview))
        {
            if (pickerItems.Count == 0)
                ImGui.TextDisabled(exceptionSearch.Trim().Length < 2
                    ? "Enter at least two characters to search the item list."
                    : "No available inventory items or matching names found.");
            foreach (var candidate in pickerItems)
            {
                var label = $"{candidate.Name}  ·  #{candidate.ItemId}";
                if (ImGui.Selectable(label, exceptionSelection?.ItemId == candidate.ItemId))
                    exceptionSelection = candidate;
            }
            ImGui.EndCombo();
        }
        var canAddException = exceptionSelection is { } choice && !config.ExcludedItemIds.Contains(choice.ItemId);
        ImGui.BeginDisabled(controller.Busy || !canAddException);
        if (ImGui.Button("Add to exceptions") && exceptionSelection is { } addChoice)
        {
            AddExclusion(addChoice.ItemId);
            exceptionSelection = null;
            exceptionBulkAddMessage = null;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(controller.Busy);
        if (ImGui.Button("Add current inventory"))
            dispatch(AddCurrentInventoryToExceptions);
        ImGui.EndDisabled();
        if (exceptionBulkAddMessage is { Length: > 0 } bulkMessage)
            ImGui.TextDisabled(bulkMessage);

        ImGui.Separator();
        ImGui.TextUnformatted($"Excluded items · {config.ExcludedItemIds.Count}");
        if (config.ExcludedItemIds.Count == 0) { ImGui.TextDisabled("No item exceptions yet."); return; }
        if (ImGui.BeginChild("##exceptionList", new Vector2(0, 220), true))
        {
            foreach (var itemId in config.ExcludedItemIds.ToArray())
            {
                var name = itemChoices.FirstOrDefault(x => x.ItemId == itemId)?.Name
                    ?? controller.ExceptionInventoryCandidates.FirstOrDefault(x => x.ItemId == itemId)?.Name
                    ?? $"Item {itemId}";
                ImGui.PushID((int)itemId);
                ImGui.TextUnformatted($"{name}  ·  #{itemId}");
                ImGui.SameLine();
                ImGui.BeginDisabled(controller.Busy);
                if (ImGui.SmallButton("Remove"))
                {
                    config.ExcludedItemIds.Remove(itemId);
                    config.Normalize();
                    save();
                }
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private void AddCurrentInventoryToExceptions()
    {
        if (controller.Busy) return;
        controller.SnapshotExceptionInventory();
        if (controller.ExceptionInventorySnapshotError is { } snapshotError)
        {
            exceptionBulkAddMessage = $"Could not add current inventory: {snapshotError}";
            return;
        }

        var itemIds = controller.ExceptionInventoryCandidates
            .Select(item => item.ItemId)
            .Distinct()
            .ToList();
        var newItemIds = itemIds.Where(itemId => !config.ExcludedItemIds.Contains(itemId)).ToList();
        if (newItemIds.Count > 0)
        {
            config.ExcludedItemIds.AddRange(newItemIds);
            config.Normalize();
            foreach (var itemId in newItemIds)
                controller.ExcludeItem(itemId);
            save();
        }

        exceptionSelection = null;
        exceptionBulkAddMessage = itemIds.Count == 0
            ? "No marketable inventory items to add; untradeable and nonmarketable items are skipped automatically."
            : $"Added {newItemIds.Count} inventory item(s); {itemIds.Count - newItemIds.Count} were already excluded.";
    }

    private void DrawNoReprice()
    {
        ImGui.TextUnformatted("Never reprice these items");
        ImGui.TextWrapped("Items on this list are still eligible for new listings. Their existing listings cannot be repriced by Update existing listings, Auto update, or the current-item price controls. Read-only price lookups still work. Use Exceptions if an item should also be skipped when creating new listings.");

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("Search item names", ref noRepriceSearch, 128);
        if (!StringComparer.CurrentCultureIgnoreCase.Equals(noRepriceSearch, noRepriceSearchCache))
            noRepriceSelection = null;
        RefreshMatches(noRepriceSearch, ref noRepriceSearchCache, ref noRepriceMatches);

        var pickerItems = noRepriceMatches.Where(item => !config.NoRepriceItemIds.Contains(item.ItemId)).ToArray();
        var preview = noRepriceSelection is { } selected
            ? $"{selected.Name}  ·  #{selected.ItemId}"
            : "Select an item to protect...";
        ImGui.SetNextItemWidth(420);
        if (ImGui.BeginCombo("Item to protect", preview))
        {
            if (pickerItems.Length == 0)
                ImGui.TextDisabled(noRepriceSearch.Trim().Length < 2
                    ? "Enter at least two characters to search the item list."
                    : "No unprotected items match that search.");
            foreach (var candidate in pickerItems)
            {
                var label = $"{candidate.Name}  ·  #{candidate.ItemId}";
                if (ImGui.Selectable(label, noRepriceSelection?.ItemId == candidate.ItemId))
                    noRepriceSelection = candidate;
            }
            ImGui.EndCombo();
        }

        var canAdd = noRepriceSelection is { } choice && !config.NoRepriceItemIds.Contains(choice.ItemId);
        ImGui.BeginDisabled(controller.Busy || !canAdd);
        if (ImGui.Button("Add to Don't reprice") && noRepriceSelection is { } addChoice)
        {
            AddNoReprice(addChoice.ItemId);
            noRepriceSelection = null;
        }
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted($"Protected items · {config.NoRepriceItemIds.Count}");
        if (config.NoRepriceItemIds.Count == 0)
        {
            ImGui.TextDisabled("No items are protected from repricing.");
            return;
        }

        if (ImGui.BeginChild("##noRepriceList", new Vector2(0, 260), true))
        {
            foreach (var itemId in config.NoRepriceItemIds.ToArray())
            {
                var name = itemChoices.FirstOrDefault(item => item.ItemId == itemId)?.Name ?? $"Item {itemId}";
                ImGui.PushID((int)itemId);
                ImGui.TextUnformatted($"{name}  ·  #{itemId}");
                ImGui.SameLine();
                ImGui.BeginDisabled(controller.Busy);
                if (ImGui.SmallButton("Remove"))
                {
                    config.NoRepriceItemIds.Remove(itemId);
                    config.Normalize();
                    save();
                }
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private void AddNoReprice(uint itemId)
    {
        if (!config.NoRepriceItemIds.Contains(itemId)) config.NoRepriceItemIds.Add(itemId);
        config.Normalize();
        save();
    }

    private void DrawBatchSelling()
    {
        ImGui.TextUnformatted("Per-item batch sizes");
        ImGui.TextWrapped("Set a maximum per listing and, for batch-only runs, an optional total limit per item. For example, per listing 5 and total 20 lists no more than 20 items in four batches. A total of 0 means unlimited. Start listing items still processes all inventory, using the per-listing size. Exclusions take priority. Existing listings are not split.");

        ImGui.BeginDisabled(controller.Busy);
        if (ImGui.Button(controller.ExceptionInventorySnapshotAt is null ? "Grab carried inventory" : "Refresh item picker"))
            dispatch(controller.SnapshotExceptionInventory);
        ImGui.EndDisabled();
        if (controller.ExceptionInventorySnapshotError is { } inventoryError)
            ImGui.TextWrapped(inventoryError);
        else if (controller.ExceptionInventorySnapshotAt is { } snapshotAt)
            ImGui.TextDisabled($"Inventory picker refreshed · {controller.ExceptionInventoryCandidates.Count} marketable item stack(s) · {snapshotAt:HH:mm:ss}.");

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("Search item names", ref batchSearch, 128);
        if (!StringComparer.CurrentCultureIgnoreCase.Equals(batchSearch, batchSearchCache))
            batchSelection = null;
        RefreshMatches(batchSearch, ref batchSearchCache, ref batchMatches);

        var inventoryItems = controller.ExceptionInventoryCandidates
            .GroupBy(item => item.ItemId)
            .Select(group => new ItemChoice(group.Key, group.First().Name));
        var pickerItems = inventoryItems.Concat(batchMatches)
            .GroupBy(item => item.ItemId).Select(group => group.First()).ToList();
        var preview = batchSelection is { } selected
            ? $"{selected.Name}  ·  #{selected.ItemId}"
            : "Select an inventory or matching item...";
        ImGui.SetNextItemWidth(420);
        if (ImGui.BeginCombo("Item to sell in batches", preview))
        {
            if (pickerItems.Count == 0)
                ImGui.TextDisabled(batchSearch.Trim().Length < 2
                    ? "Grab carried inventory or enter at least two characters to search."
                    : "No inventory items or matching item names found.");
            foreach (var candidate in pickerItems)
            {
                var alreadyConfigured = config.BatchSaleQuantities.ContainsKey(candidate.ItemId);
                var label = $"{candidate.Name}  ·  #{candidate.ItemId}" + (alreadyConfigured ? " (batch size set)" : "");
                ImGui.BeginDisabled(alreadyConfigured);
                if (ImGui.Selectable(label, batchSelection?.ItemId == candidate.ItemId))
                    batchSelection = candidate;
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(140);
        ImGui.InputInt("Maximum items per listing", ref batchQuantityInput);
        batchQuantityInput = Math.Clamp(batchQuantityInput, 1, 9_999);
        ImGui.SetNextItemWidth(180);
        ImGui.InputInt("Maximum total to list per run (0 = unlimited)", ref batchMaximumTotalInput);
        batchMaximumTotalInput = Math.Clamp(batchMaximumTotalInput, 0, 999_999_999);
        var canAddBatchItem = batchSelection is { } choice && !config.BatchSaleQuantities.ContainsKey(choice.ItemId);
        ImGui.BeginDisabled(controller.Busy || !canAddBatchItem);
        if (ImGui.Button("Add to batch list") && batchSelection is { } addChoice)
        {
            config.BatchSaleQuantities[addChoice.ItemId] = (uint)batchQuantityInput;
            config.BatchSaleMaxQuantities[addChoice.ItemId] = (uint)batchMaximumTotalInput;
            config.Normalize();
            save();
            batchSelection = null;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(controller.Busy);
        if (ImGui.Button("Add current inventory"))
            dispatch(AddCurrentInventoryToBatchSelling);
        ImGui.EndDisabled();
        if (batchBulkAddMessage is { Length: > 0 } bulkMessage)
            ImGui.TextDisabled(bulkMessage);

        ImGui.Separator();
        ImGui.TextUnformatted($"Items sold in batches · {config.BatchSaleQuantities.Count}");
        if (config.BatchSaleQuantities.Count == 0)
        {
            ImGui.TextDisabled("No batch sizes set. Every item will be listed as one full stack.");
            return;
        }
        if (!ImGui.BeginTable("##batchSaleItems", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, 220))) return;
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Max per listing", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Max total / run", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var entry in config.BatchSaleQuantities.OrderBy(entry => itemChoices.FirstOrDefault(item => item.ItemId == entry.Key)?.Name ?? $"Item {entry.Key}").ToArray())
        {
            var name = itemChoices.FirstOrDefault(item => item.ItemId == entry.Key)?.Name
                ?? controller.ExceptionInventoryCandidates.FirstOrDefault(item => item.ItemId == entry.Key)?.Name
                ?? $"Item {entry.Key}";
            ImGui.PushID((int)entry.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextWrapped($"{name}  ·  #{entry.Key}");
            ImGui.TableNextColumn();
            var quantity = (int)Math.Min(entry.Value, 9_999);
            ImGui.SetNextItemWidth(110);
            ImGui.BeginDisabled(controller.Busy);
            if (ImGui.InputInt("##batchQuantity", ref quantity))
            {
                config.BatchSaleQuantities[entry.Key] = (uint)Math.Clamp(quantity, 1, 9_999);
                config.Normalize();
                save();
            }
            ImGui.TableNextColumn();
            var maximumTotal = (int)Math.Min(config.BatchSaleMaxQuantities.GetValueOrDefault(entry.Key), 999_999_999);
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("##batchMaximumTotal", ref maximumTotal))
            {
                config.BatchSaleMaxQuantities[entry.Key] = (uint)Math.Clamp(maximumTotal, 0, 999_999_999);
                config.Normalize();
                save();
            }
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Remove"))
            {
                config.BatchSaleQuantities.Remove(entry.Key);
                config.BatchSaleMaxQuantities.Remove(entry.Key);
                save();
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void AddCurrentInventoryToBatchSelling()
    {
        if (controller.Busy) return;
        controller.SnapshotExceptionInventory();
        if (controller.ExceptionInventorySnapshotError is { } snapshotError)
        {
            batchBulkAddMessage = $"Could not add current inventory: {snapshotError}";
            return;
        }

        var inventoryItems = controller.ExceptionInventoryCandidates
            .Where(item => !config.ExcludedItemIds.Contains(item.ItemId))
            .GroupBy(item => item.ItemId)
            .Select(group => group.First())
            .ToArray();
        var added = 0;
        foreach (var item in inventoryItems)
        {
            if (config.BatchSaleQuantities.ContainsKey(item.ItemId)) continue;
            config.BatchSaleQuantities[item.ItemId] = (uint)batchQuantityInput;
            config.BatchSaleMaxQuantities[item.ItemId] = (uint)batchMaximumTotalInput;
            added++;
        }
        config.Normalize();
        if (added > 0) save();
        batchBulkAddMessage = inventoryItems.Length == 0
            ? "No eligible carried inventory items to add; excluded and unmarketable items are skipped."
            : $"Added {added} item(s) to batch selling; {inventoryItems.Length - added} were already on the list. New entries use the current per-listing and total limits.";
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
    {
        var history = snapshot.Source != PriceSource.Universalis ? ""
            : snapshot.MostRecentSaleAt is { } saleAt ? $" · last sale {Age(saleAt)} ago"
            : " · no sale in the last 20 days";
        var cached = snapshot.WasCached && snapshot.RetrievedAt is { } retrievedAt
            ? $" · cached {Age(retrievedAt)} ago" : "";
        ImGui.TextUnformatted($"{snapshot.Source}: {Age(snapshot.ObservedAt)} old · {snapshot.ObservedAt.ToLocalTime():HH:mm:ss}{history}{cached}");
    }

    private static string Age(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        return age.TotalMinutes >= 1 ? $"{Math.Max(0, (int)age.TotalMinutes)}m" : $"{Math.Max(0, (int)age.TotalSeconds)}s";
    }
}
