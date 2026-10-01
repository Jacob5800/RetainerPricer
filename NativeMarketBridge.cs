using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Runtime.InteropServices;

namespace RetainerPricer;

public sealed record MarketSession(ulong ContentId, ulong RetainerId, uint WorldId, string WorldName);

public sealed record SellItem(MarketSession Session, uint ItemId, string Name, bool IsHq,
    uint Quantity, uint CurrentPrice, int InventoryType, int Slot, long DialogGeneration = 0)
{
    public bool IsExisting => InventoryType == (int)FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerMarket;
}

/// <summary>
/// Native UI operations must be called on the framework thread. No background task may retain native pointers.
/// A successful submit means the native Confirm button was dispatched, not that the server accepted the change.
/// </summary>
public sealed unsafe class NativeMarketBridge : IDisposable
{
    private const uint MaximumPrice = 999_999_999;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IPlayerState player;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly OpenRetainerSellDelegate? openRetainerSell;
    private readonly bool retainerFieldsSupported;
    private Hook<RequestResultDelegate>? resultHook;
    private Hook<EndRequestDelegate>? endHook;
    private SellItem? compareItem;
    private PriceSnapshot? localSnapshot;
    private bool resultReceived;
    private bool requestComplete;
    private int expectedListingCount;
    private string localError = "Compare prices in the current sell window first.";
    private nint sellAddress;
    private bool sellWasVisible;
    private bool disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RequestResultDelegate(InfoProxyItemSearch* proxy, byte count, int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndRequestDelegate(InfoProxyItemSearch* proxy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OpenRetainerSellDelegate(AgentRetainer* agent, InventoryType inventoryType, ushort inventorySlot);

    public long DialogGeneration { get; private set; } = 1;
    public string? LocalAvailabilityError { get; private set; }
    public string? RetainerAvailabilityError { get; private set; }

    public NativeMarketBridge(IGameGui gameGui, IDataManager data, IPlayerState player,
        IAddonLifecycle lifecycle, IGameInteropProvider interop, ISigScanner scanner, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.data = data;
        this.player = player;
        this.lifecycle = lifecycle;
        this.log = log;
        retainerFieldsSupported = RetainerAgentView.IsSupported;
        lifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnSellLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSell", OnSellLifecycle);
        if (retainerFieldsSupported)
        {
            try
            {
                // This is the call-site signature attached to AgentRetainer.OpenRetainerSell in FFXIVClientStructs.
                // The official helper resolves its relative E8 target; verify the one match and opcode before binding.
                const string openSellCall = "E8 ?? ?? ?? ?? EB ?? 48 83 BF ?? ?? ?? ?? ?? 74 ?? 8B CE";
                var matches = scanner.ScanAllText(openSellCall);
                if (matches.Length == 1 && *(byte*)matches[0] == 0xE8)
                {
                    var relativeOffset = *(int*)(matches[0] + 1);
                    var target = scanner.ResolveRelativeAddress(matches[0] + 5, relativeOffset);
                    var textStart = scanner.TextSectionBase;
                    if (target >= textStart && target < textStart + scanner.TextSectionSize)
                        openRetainerSell = Marshal.GetDelegateForFunctionPointer<OpenRetainerSellDelegate>(target);
                }
            }
            catch (Exception ex) { log.Warning(ex, "Retainer stock-opening callback is not available on this game build."); }
        }
        if (!retainerFieldsSupported)
            RetainerAvailabilityError = "This FFXIV client layout differs from its verified retainer fields. Reload a matching Dalamud SDK before pricing listings.";
        else if (openRetainerSell is null)
            RetainerAvailabilityError = "This game build's retainer item selector could not be verified. Batch updates are disabled.";
        try
        {
            // EndRequest is the documented all-pages-complete callback. ProcessRequestResult supplies
            // the response status and exact expected row count, including the empty-market case.
            resultHook = interop.HookFromAddress<RequestResultDelegate>(
                InfoProxyItemSearch.Addresses.ProcessRequestResult.Value, OnRequestResult);
            endHook = interop.HookFromAddress<EndRequestDelegate>(
                (nint)InfoProxyItemSearch.StaticVirtualTablePointer->EndRequest, OnEndRequest);
            resultHook.Enable();
            endHook.Enable();

        }
        catch (Exception ex)
        {
            resultHook?.Dispose();
            endHook?.Dispose();
            resultHook = null;
            endHook = null;
            LocalAvailabilityError = "Local price reading is unavailable on this game build. Use Universalis.";
            log.Error(ex, "Could not initialize complete local market response tracking.");
        }
    }

    public bool TryGetSession(out MarketSession session, out string error)
    {
        session = null!;
        error = "Open a retainer's sale list on your home world.";
        if (!retainerFieldsSupported) { error = RetainerAvailabilityError ?? "Retainer data is unavailable on this client build."; return false; }
        if (disposed || !player.IsLoaded || player.ContentId == 0 || player.CurrentWorld.RowId == 0 ||
            player.CurrentWorld.RowId != player.HomeWorld.RowId)
            return false;
        var manager = RetainerManager.Instance();
        var agent = AgentRetainer.Instance();
        var agentView = RetainerAgentView.For(agent);
        var list = (AtkUnitBase*)gameGui.GetAddonByName("RetainerSellList").Address;
        if (manager == null || !manager->IsReady || manager->LastSelectedRetainerId == 0 ||
            agentView == null || !agent->IsAgentActive() || list == null || !list->IsReady ||
            agentView->SellListAddonId != list->Id)
            return false;
        var active = manager->GetActiveRetainer();
        if (active == null || active->RetainerId != manager->LastSelectedRetainerId)
            return false;
        // The parent list may be visually hidden by its modal sell/compare window.
        var sell = (AtkUnitBase*)gameGui.GetAddonByName("RetainerSell").Address;
        if (!list->IsVisible && (sell == null || !sell->IsReady))
            return false;
        session = new MarketSession(player.ContentId, active->RetainerId,
            player.CurrentWorld.RowId, player.CurrentWorld.Value.Name.ToString());
        error = string.Empty;
        return true;
    }

    public bool TryReadSellItem(out SellItem item, out string error)
    {
        item = null!;
        if (!TryGetSession(out var session, out error)) return false;
        var addon = GetSellAddon();
        var agent = AgentRetainer.Instance();
        var agentView = RetainerAgentView.For(agent);
        if (addon == null || agentView == null || addon->Id != agentView->SellAddonId ||
            addon->AskingPrice == null || addon->Quantity == null)
        {
            error = "Open the item's Set Sale Price window.";
            return false;
        }
        if (!TryGetStock(agentView->SellInventoryType, agentView->SellInventorySlot, out var stock, out error))
            return false;
        var quantity = addon->Quantity->Value;
        var price = addon->AskingPrice->Value;
        if (quantity <= 0 || quantity > stock->GetQuantity() || price < 0 || price > MaximumPrice ||
            quantity != agentView->SellQuantity || price != agentView->SellUnitPrice)
        {
            error = "The sell window is still updating; wait a moment.";
            return false;
        }
        var itemId = stock->GetBaseItemId();
        item = new SellItem(session, itemId, ItemName(itemId), stock->IsHighQuality(),
            (uint)quantity, (uint)price, (int)agentView->SellInventoryType,
            agentView->SellInventorySlot, DialogGeneration);
        error = string.Empty;
        return true;
    }

    public IReadOnlyList<SellItem> ReadExistingListings(out string error)
    {
        var result = new List<SellItem>();
        if (!TryGetSession(out var session, out error)) return result;
        var inventory = InventoryManager.Instance();
        var agent = AgentRetainer.Instance();
        var agentView = RetainerAgentView.For(agent);
        var container = inventory == null ? null : inventory->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded || container->Size != 20 || agentView == null ||
            agentView->SellListCount is < 0 or > 20)
        {
            error = "The retainer's stock is still loading.";
            return result;
        }
        var usedSlots = new HashSet<ushort>();
        for (var index = 0; index < agentView->SellListCount; index++)
        {
            ref var entry = ref agentView->SellList[index];
            var slot = entry.InventorySlot;
            if (slot >= 20 || !usedSlots.Add(slot))
            {
                error = "The sale list changed while it was being read. Refresh it.";
                return [];
            }
            var stock = container->GetInventorySlot(slot);
            if (stock == null || stock->IsEmpty() || stock->GetQuantity() != entry.Quantity ||
                stock->GetItemId() != entry.ItemId && stock->GetBaseItemId() != entry.ItemId)
            {
                error = "The sale list and inventory do not agree. Reopen the sale list.";
                return [];
            }
            var price = inventory->GetRetainerMarketPrice((short)slot);
            if (price is 0 or > MaximumPrice)
            {
                error = "A listing has no valid loaded price. Refresh the sale list.";
                return [];
            }
            var id = stock->GetBaseItemId();
            result.Add(new SellItem(session, id, ItemName(id), stock->IsHighQuality(),
                stock->GetQuantity(), (uint)price, (int)InventoryType.RetainerMarket, slot));
        }
        error = string.Empty;
        return result;
    }

    public IReadOnlySet<ulong> OwnRetainerIds()
    {
        var ids = new HashSet<ulong>();
        if (!player.IsLoaded) return ids;
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) return ids;
        var count = manager->GetRetainerCount();
        if (count > 10) return ids;
        for (uint index = 0; index < count; index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);
            if (retainer != null && retainer->RetainerId != 0) ids.Add(retainer->RetainerId);
        }
        return ids;
    }

    public bool TryFillPrice(SellItem expected, uint price, out string error)
    {
        if (price is 0 or > MaximumPrice)
        {
            error = "Price must be between 1 and 999,999,999 gil.";
            return false;
        }
        if (!MatchesCurrentDialog(expected, true, out _, out error)) return false;
        var addon = GetSellAddon();
        var agent = AgentRetainer.Instance();
        var agentView = RetainerAgentView.For(agent);
        if (addon == null || agentView == null || addon->AskingPrice == null ||
            price > agentView->SellPriceLimit)
        {
            error = "The game does not currently allow this price.";
            return false;
        }
        // Trigger the numeric component's normal value callback so the agent receives the price too.
        addon->AskingPrice->InnerSetValue((int)price, true, false);
        if (addon->AskingPrice->Value != price || agentView->SellUnitPrice != price)
        {
            error = "The game's price field did not accept the update. Nothing was confirmed.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public bool TryOpenExisting(SellItem expected, out string error)
    {
        if (!expected.IsExisting)
        {
            error = "This is not an existing retainer listing.";
            return false;
        }
        if (GetSellAddon() != null || IsAddonReady("ItemSearchResult"))
        {
            error = "Close the current price or comparison window first.";
            return false;
        }
        if (!TryReadExistingPrice(expected, out var currentPrice, out error)) return false;
        if (currentPrice != expected.CurrentPrice)
        {
            error = "This listing's price changed. Review it again.";
            return false;
        }
        if (openRetainerSell is null) { error = "This FFXIV client build does not expose its verified retainer item selector."; return false; }
        var agent = AgentRetainer.Instance();
        if (RetainerAgentView.For(agent) == null || !agent->IsAgentActive())
        { error = "The active retainer changed. Reopen its sale list and retry."; return false; }
        openRetainerSell(agent, InventoryType.RetainerMarket, checked((ushort)expected.Slot));
        error = string.Empty;
        return true;
    }

    public bool TrySetExistingPrice(SellItem expected, uint price, out string error)
    {
        if (!expected.IsExisting || expected.DialogGeneration == 0)
        {
            error = "Open and recheck the existing listing before applying its price.";
            return false;
        }
        if (!TryFillPrice(expected, price, out error)) return false;
        if (!MatchesCurrentDialog(expected with { CurrentPrice = price }, true, out _, out error)) return false;
        var addon = GetSellAddon();
        if (addon == null) { error = "The sell window closed."; return false; }
        return ClickRegisteredButton(addon->Confirm, (AtkUnitBase*)addon, out error);
    }

    public bool TryReadExistingPrice(SellItem expected, out uint price, out string error)
    {
        price = 0;
        var listings = ReadExistingListings(out error);
        if (error.Length > 0) return false;
        var current = listings.FirstOrDefault(x => SameStock(x, expected));
        if (current == null)
        {
            error = "The retainer, item, quality, quantity or inventory slot changed.";
            return false;
        }
        price = current.CurrentPrice;
        return true;
    }

    public bool RequestCompare(SellItem expected, out string error)
    {
        if (LocalAvailabilityError != null) { error = LocalAvailabilityError; return false; }
        if (!MatchesCurrentDialog(expected, true, out _, out error)) return false;
        var proxy = InfoProxyItemSearch.Instance();
        var addon = GetSellAddon();
        if (proxy == null || proxy->WaitingForListings || IsAddonReady("ItemSearchResult") || addon == null)
        {
            error = "Wait for or close the current market comparison before starting another.";
            return false;
        }
        compareItem = expected;
        localSnapshot = null;
        resultReceived = false;
        requestComplete = false;
        expectedListingCount = -1;
        localError = "Waiting for the complete local market response.";
        if (!ClickRegisteredButton(addon->ComparePrices, (AtkUnitBase*)addon, out error))
        {
            compareItem = null;
            return false;
        }
        return true;
    }

    public bool TryGetLocalSnapshot(SellItem expected, out PriceSnapshot? snapshot, out string error)
    {
        snapshot = null;
        error = "";
        if (LocalAvailabilityError is not null) { error = LocalAvailabilityError; return false; }
        if (compareItem == null || !SameStock(compareItem, expected) ||
            compareItem.DialogGeneration != expected.DialogGeneration ||
            !MatchesCurrentDialog(expected, true, out _, out error)) return false;
        if (localSnapshot == null)
        {
            if (requestComplete && !string.IsNullOrEmpty(localError)) error = localError;
            return false;
        }
        snapshot = localSnapshot;
        error = string.Empty;
        return true;
    }

    public bool TryCloseCompare(SellItem expected, out string error)
    {
        if (!MatchesCurrentDialog(expected, false, out _, out error)) return false;
        var comparison = (AtkUnitBase*)gameGui.GetAddonByName("ItemSearchResult").Address;
        if (comparison == null || !comparison->IsReady) { error = string.Empty; return true; }
        var search = AgentItemSearch.Instance();
        if (compareItem == null || !SameStock(compareItem, expected) ||
            compareItem.DialogGeneration != expected.DialogGeneration || search == null ||
            search->ResultItemId != expected.ItemId)
        {
            error = "The comparison window belongs to another item; it was left open.";
            return false;
        }
        comparison->Close(true);
        error = string.Empty;
        return true;
    }

    public bool TryClosePriceWindows(SellItem expected, out string error)
    {
        if (!TryCloseCompare(expected, out error)) return false;
        if (!MatchesCurrentDialog(expected, false, out _, out error)) return false;
        var addon = GetSellAddon();
        if (addon == null) { error = "The price window closed."; return false; }
        return ClickRegisteredButton(addon->Cancel, (AtkUnitBase*)addon, out error);
    }

    private void OnSellLifecycle(AddonEvent eventType, AddonArgs args)
    {
        DialogGeneration++;
        sellAddress = eventType == AddonEvent.PostSetup ? args.Addon.Address : 0;
        sellWasVisible = false;
        compareItem = null;
        localSnapshot = null;
        resultReceived = false;
        requestComplete = false;
    }

    private AddonRetainerSell* GetSellAddon()
    {
        var addon = (AddonRetainerSell*)gameGui.GetAddonByName("RetainerSell").Address;
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        {
            sellWasVisible = false;
            return null;
        }
        var agentView = RetainerAgentView.For(AgentRetainer.Instance());
        if (agentView == null || agentView->SellAddonId != addon->Id) return null;
        if (!sellWasVisible)
        {
            sellWasVisible = true;
            DialogGeneration++;
            compareItem = null;
            localSnapshot = null;
        }
        if (sellAddress != (nint)addon)
        {
            sellAddress = (nint)addon;
            DialogGeneration++;
        }
        return addon;
    }

    private bool IsAddonReady(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsReady;
    }

    private bool MatchesCurrentDialog(SellItem expected, bool requireSamePrice, out SellItem current, out string error)
    {
        if (!TryReadSellItem(out current, out error)) return false;
        if (expected.DialogGeneration == 0 || expected.DialogGeneration != current.DialogGeneration ||
            !SameStock(expected, current) || requireSamePrice && expected.CurrentPrice != current.CurrentPrice)
        {
            error = "The item, price, quantity, retainer or sell window changed. Get a fresh quote.";
            return false;
        }
        return true;
    }

    private static bool SameStock(SellItem a, SellItem b) =>
        a.Session.ContentId == b.Session.ContentId && a.Session.RetainerId == b.Session.RetainerId &&
        a.Session.WorldId == b.Session.WorldId && a.ItemId == b.ItemId && a.IsHq == b.IsHq &&
        a.Quantity == b.Quantity && a.InventoryType == b.InventoryType && a.Slot == b.Slot;

    private static bool TryGetStock(InventoryType type, int slot, out InventoryItem* stock, out string error)
    {
        stock = null;
        error = "The item's inventory slot is not loaded.";
        var inventory = InventoryManager.Instance();
        if (inventory == null || slot < 0) return false;
        var container = inventory->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded || slot >= container->Size) return false;
        stock = container->GetInventorySlot(slot);
        if (stock == null || stock->IsEmpty() || stock->GetBaseItemId() == 0) return false;
        error = string.Empty;
        return true;
    }

    private string ItemName(uint id) => data.GetExcelSheet<Item>().GetRow(id).Name.ToString();

    private static bool ClickRegisteredButton(AtkComponentButton* button, AtkUnitBase* owner, out string error)
    {
        error = "The game's button is not ready or its registered action could not be identified.";
        if (button == null || button->OwnerNode == null || !button->IsEnabled || owner == null || !owner->IsReady)
            return false;
        var node = &button->OwnerNode->AtkResNode;
        AtkEvent* matched = null;
        var count = 0;
        for (var evt = node->AtkEventManager.Event; evt != null && count++ < 64; evt = evt->NextEvent)
        {
            if (evt->State.EventType != AtkEventType.ButtonClick || evt->Listener != (AtkEventListener*)owner ||
                (evt->State.StateFlags & AtkEventStateFlags.IsGlobalEvent) != 0) continue;
            if (matched != null) return false;
            matched = evt;
        }
        if (matched == null || count >= 64) return false;
        // Preserve the registered target and event parameter; never assume undocumented callback numbers.
        var copy = *matched;
        copy.NextEvent = null;
        copy.State.ReturnFlags = 0;
        copy.State.StateFlags &= ~(AtkEventStateFlags.Handled | AtkEventStateFlags.HasReturnFlags);
        AtkEventData eventData = default;
        matched->Listener->ReceiveEvent(AtkEventType.ButtonClick, checked((int)matched->Param), &copy, &eventData);
        error = string.Empty;
        return true;
    }

    private void OnRequestResult(InfoProxyItemSearch* proxy, byte count, int error)
    {
        try
        {
            if (compareItem != null && proxy != null && proxy->SearchItemId == compareItem.ItemId)
            {
                resultReceived = error == 0;
                requestComplete = error != 0;
                expectedListingCount = count;
                localSnapshot = null;
                localError = error == 0 ? "Waiting for all local listings." :
                    $"The local market request failed (0x{error:X8}). Wait before retrying.";
            }
        }
        catch (Exception ex) { log.Error(ex, "Tracking local market request status failed."); }
        resultHook!.Original(proxy, count, error);
    }

    private void OnEndRequest(InfoProxyItemSearch* proxy)
    {
        endHook!.Original(proxy);
        try
        {
            if (compareItem == null || proxy == null || proxy->SearchItemId != compareItem.ItemId) return;
            if (!resultReceived || expectedListingCount is < 0 or > 100 ||
                proxy->ListingCount != expectedListingCount || proxy->WaitingForListings ||
                !MatchesCurrentDialog(compareItem, true, out _, out _))
            {
                localSnapshot = null;
                localError = "The complete local response could not be verified. Request prices again.";
                requestComplete = true;
                return;
            }
            var rows = new List<MarketListing>(expectedListingCount);
            for (var i = 0; i < expectedListingCount; i++)
            {
                ref var row = ref proxy->Listings[i];
                if (row.ItemId != compareItem.ItemId || row.UnitPrice is 0 or > MaximumPrice ||
                    row.Quantity == 0 || row.ListingId == 0)
                {
                    localSnapshot = null;
                    localError = "A local listing was incomplete or invalid. Request prices again.";
                    requestComplete = true;
                    return;
                }
                rows.Add(new MarketListing(row.ItemId, row.IsHqItem, row.UnitPrice,
                    row.Quantity, row.RetainerId, row.IsMannequin));
            }
            localSnapshot = new PriceSnapshot(compareItem.ItemId, compareItem.Session.WorldId,
                PriceSource.Local, DateTimeOffset.UtcNow, rows, true);
            localError = string.Empty;
            requestComplete = true;
        }
        catch (Exception ex)
        {
            localSnapshot = null;
            localError = "The local response could not be verified. Request prices again.";
            requestComplete = true;
            log.Error(ex, "Copying complete market response failed.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", OnSellLifecycle);
        lifecycle.UnregisterListener(AddonEvent.PreFinalize, "RetainerSell", OnSellLifecycle);
        endHook?.Dispose();
        resultHook?.Dispose();
    }
}
