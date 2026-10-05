using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Runtime.InteropServices;

namespace RetainerPricer;

public sealed record MarketSession(ulong ContentId, ulong RetainerId, uint WorldId, string WorldName,
    string? DataCenterName = null);

public sealed record RetainerIdentity(ulong RetainerId, string Name);

internal sealed record RetainerPickerEntry(RetainerIdentity Retainer, bool IsAvailable);

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
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly OpenRetainerSellDelegate? openRetainerSell;
    private readonly SellItemToVendorDelegate? sellItemToVendor;
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SellItemToVendorDelegate(uint inventorySlot, InventoryType inventoryType, uint unused);

    public long DialogGeneration { get; private set; } = 1;
    public string? LocalAvailabilityError { get; private set; }
    public string? RetainerAvailabilityError { get; private set; }
    public string? ItemSelectorAvailabilityError { get; private set; }
    public string? VendorSaleAvailabilityError { get; private set; }
    public bool IsComparisonVisible => IsAddonVisible("ItemSearchResult");
    public bool IsSellWindowVisible => GetSellAddon() != null;
    public bool IsClientStateUnavailable => !clientState.IsLoggedIn || !player.IsLoaded || player.ContentId == 0 ||
        condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
        condition[ConditionFlag.LoggingOut] || condition[ConditionFlag.SystemError];
    public bool IsRetainerPickerVisible => IsAddonVisible("RetainerList");
    public bool IsRetainerMenuVisible => IsAddonVisible("SelectString");
    public bool IsRetainerDialogueVisible => IsAddonVisible("Talk");
    public bool IsRetainerSellListVisible => IsAddonVisible("RetainerSellList");
    public bool IsLocalSearchBusy
    {
        get
        {
            var proxy = InfoProxyItemSearch.Instance();
            return proxy != null && proxy->WaitingForListings;
        }
    }

    public MarketWorld? GetHomeWorld()
    {
        if (disposed || !player.IsLoaded || player.ContentId == 0 || player.HomeWorld.RowId == 0) return null;
        return new MarketWorld(player.HomeWorld.RowId, player.HomeWorld.Value.Name.ToString(), GetHomeDataCenterName());
    }

    public NativeMarketBridge(IGameGui gameGui, IDataManager data, IPlayerState player,
        IClientState clientState, ICondition condition,
        IAddonLifecycle lifecycle, IGameInteropProvider interop, ISigScanner scanner, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.data = data;
        this.player = player;
        this.clientState = clientState;
        this.condition = condition;
        this.lifecycle = lifecycle;
        this.log = log;
        retainerFieldsSupported = RetainerAgentView.IsSupported;
        lifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", OnSellLifecycle);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "RetainerSell", OnSellLifecycle);
        if (retainerFieldsSupported)
        {
            try
            {
                // Use the FFXIVClientStructs AgentRetainer.OpenRetainerSell call-site signature and Dalamud's
                // standard ScanText resolver, which follows the E8 call to its target. ScanAllText can find
                // additional callers that share this pattern and reject a valid client when their targets differ.
                const string openSellCall = "E8 ?? ?? ?? ?? EB ?? 48 83 BF ?? ?? ?? ?? ?? 74 ?? 8B CE";
                var target = scanner.ScanText(openSellCall);
                openRetainerSell = Marshal.GetDelegateForFunctionPointer<OpenRetainerSellDelegate>(target);
            }
            catch (Exception ex) { log.Warning(ex, "Retainer stock-opening callback is not available on this game build."); }
        }
        if (!retainerFieldsSupported)
            RetainerAvailabilityError = "This FFXIV client layout differs from its verified retainer fields. Reload a matching Dalamud SDK before pricing listings.";
        else if (openRetainerSell is null)
            ItemSelectorAvailabilityError = "The game's retainer item selector could not be verified. Price scanning is available, but automatic applying is disabled on this client build.";
        try
        {
            // AutoRetainer uses this game shop action for NPC sales instead of selecting an inventory context-menu row.
            const string sellItemToShop = "48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B F2 8B E9";
            var target = scanner.ScanText(sellItemToShop);
            sellItemToVendor = Marshal.GetDelegateForFunctionPointer<SellItemToVendorDelegate>(target);
        }
        catch (Exception ex)
        {
            VendorSaleAvailabilityError = "The game's NPC vendor sale action could not be verified on this client build.";
            log.Warning(ex, "NPC vendor sale action is not available on this game build.");
        }
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
            player.CurrentWorld.RowId, player.CurrentWorld.Value.Name.ToString(), GetHomeDataCenterName());
        error = string.Empty;
        return true;
    }

    public bool IsSessionIdentityChanged(MarketSession expected)
    {
        if (!player.IsLoaded || player.ContentId == 0 || player.ContentId != expected.ContentId ||
            player.CurrentWorld.RowId != expected.WorldId)
            return true;

        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) return false;
        if (manager->LastSelectedRetainerId != 0 && manager->LastSelectedRetainerId != expected.RetainerId)
            return true;

        var agent = AgentRetainer.Instance();
        if (RetainerAgentView.For(agent) is null || !agent->IsAgentActive()) return false;
        var active = manager->GetActiveRetainer();
        return active != null && active->RetainerId != expected.RetainerId;
    }

    public bool IsCharacterOrWorldChanged(ulong expectedContentId, uint expectedWorldId) =>
        !player.IsLoaded || player.ContentId == 0 || player.ContentId != expectedContentId ||
        player.CurrentWorld.RowId != expectedWorldId || player.HomeWorld.RowId != expectedWorldId;

    public bool TryGetSelectedRetainerId(out ulong retainerId)
    {
        retainerId = 0;
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady || manager->LastSelectedRetainerId == 0) return false;
        retainerId = manager->LastSelectedRetainerId;
        return true;
    }

    public bool TryGetCharacterContext(out ulong contentId, out uint worldId, out string error)
    {
        contentId = 0;
        worldId = 0;
        if (disposed || !player.IsLoaded || player.ContentId == 0 || player.CurrentWorld.RowId == 0)
        { error = "Log in before starting Auto update."; return false; }
        if (player.CurrentWorld.RowId != player.HomeWorld.RowId)
        { error = "Auto update is available only while you are on your home world."; return false; }
        contentId = player.ContentId;
        worldId = player.CurrentWorld.RowId;
        error = string.Empty;
        return true;
    }

    private string? GetHomeDataCenterName()
    {
        if (!player.IsLoaded || player.HomeWorld.RowId == 0) return null;
        var dataCenter = player.HomeWorld.Value.DataCenter;
        return dataCenter.RowId == 0 ? null : dataCenter.Value.Name.ToString();
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
        if (IsBound(stock))
        {
            error = "This item is bound and cannot be listed on the marketboard.";
            return false;
        }
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

    public bool TryReadSavedGearsetItemIds(out IReadOnlySet<uint> itemIds, out string error)
    {
        itemIds = new HashSet<uint>();
        error = "Saved gear-set data is not ready. Wait for your character to finish loading, then retry.";
        if (disposed || !player.IsLoaded || player.ContentId == 0) return false;

        var module = RaptureGearsetModule.Instance();
        if (module == null) return false;

        var protectedIds = new HashSet<uint>();
        var entries = module->Entries;
        for (var gearsetIndex = 0; gearsetIndex < 100; gearsetIndex++)
        {
            ref var gearset = ref entries[gearsetIndex];
            if ((gearset.Flags & RaptureGearsetModule.GearsetFlag.Exists) == RaptureGearsetModule.GearsetFlag.None)
                continue;

            var items = gearset.Items;
            for (var slot = 0; slot < 14; slot++)
            {
                var itemId = items[slot].ItemId;
                if (itemId != 0) protectedIds.Add(itemId);
            }
        }

        itemIds = protectedIds;
        error = string.Empty;
        return true;
    }

    public IReadOnlyList<CarriedItemCandidate> ReadCarriedInventory(IReadOnlySet<uint> marketableItemIds,
        IReadOnlySet<uint> excludedItemIds, out int exceptionSkipped, out int unmarketableSkipped, out string error)
    {
        var result = new List<CarriedItemCandidate>();
        exceptionSkipped = 0;
        unmarketableSkipped = 0;
        error = "Log in before taking an inventory snapshot.";
        if (disposed || !player.IsLoaded || player.ContentId == 0) return result;
        var inventory = InventoryManager.Instance();
        if (inventory == null) { error = "The character inventory is not available yet."; return result; }

        InventoryType[] carriedContainers = [InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4];
        foreach (var type in carriedContainers)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                error = "The carried inventory is still loading. Try the snapshot again in a moment.";
                return [];
            }
            for (var slot = 0; slot < container->Size; slot++)
            {
                var stock = container->GetInventorySlot(slot);
                if (stock == null || stock->IsEmpty() || stock->GetQuantity() == 0) continue;
                var itemId = stock->GetBaseItemId();
                if (itemId == 0) continue;
                // Spiritbond indicates gear is bound to this character, so it can no longer be
                // transferred or listed even when the base Item row is normally marketable.
                if (IsBound(stock)) { unmarketableSkipped++; continue; }
                if (!marketableItemIds.Contains(itemId)) { unmarketableSkipped++; continue; }
                if (excludedItemIds.Contains(itemId)) { exceptionSkipped++; continue; }
                var name = ItemName(itemId);
                if (string.IsNullOrWhiteSpace(name)) { unmarketableSkipped++; continue; }
                result.Add(new CarriedItemCandidate(itemId, name, stock->IsHighQuality(),
                    stock->GetQuantity(), (int)type, slot));
            }
        }
        error = string.Empty;
        return result;
    }

    public bool TryReadCarriedStackQuantity(CarriedItemCandidate expected, out uint quantity, out string error)
    {
        quantity = 0;
        var type = (InventoryType)expected.InventoryType;
        if (type is not (InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4))
        { error = "The original carried inventory slot is invalid."; return false; }
        var inventory = InventoryManager.Instance();
        var container = inventory == null ? null : inventory->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded || expected.Slot < 0 || expected.Slot >= container->Size)
        { error = "The carried inventory is still loading."; return false; }
        var stock = container->GetInventorySlot(expected.Slot);
        if (stock == null)
        { error = "The original carried inventory slot is unavailable."; return false; }
        if (stock->IsEmpty()) { error = string.Empty; return true; }
        if (stock->GetBaseItemId() != expected.ItemId || stock->IsHighQuality() != expected.IsHq)
        { error = "The original inventory slot now contains a different item. The batch stopped."; return false; }
        if (IsBound(stock)) { error = $"{expected.Name} is bound and cannot be listed."; return false; }
        quantity = stock->GetQuantity();
        error = string.Empty;
        return true;
    }

    public IReadOnlySet<ulong> OwnRetainerIds()
    {
        return TryGetOwnRetainerIds(out var ids) ? ids : new HashSet<ulong>();
    }

    public bool TryGetOwnRetainerIds(out IReadOnlySet<ulong> ids)
    {
        var loadedIds = new HashSet<ulong>();
        ids = loadedIds;
        if (!player.IsLoaded || player.ContentId == 0) return false;
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) return false;
        var count = manager->GetRetainerCount();
        if (count > 10) return false;
        for (uint index = 0; index < count; index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);
            if (retainer == null || retainer->RetainerId == 0) return false;
            loadedIds.Add(retainer->RetainerId);
        }
        ids = loadedIds;
        return true;
    }

    public bool TryGetOwnRetainers(out IReadOnlyList<RetainerIdentity> retainers)
    {
        var result = new List<RetainerIdentity>();
        retainers = result;
        if (!player.IsLoaded || player.ContentId == 0) return false;
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) return false;
        var count = manager->GetRetainerCount();
        if (count is 0 or > 10) return false;
        var ids = new HashSet<ulong>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (uint index = 0; index < count; index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);
            if (retainer == null || retainer->RetainerId == 0) return false;
            var name = retainer->NameString;
            if (string.IsNullOrWhiteSpace(name) || !ids.Add(retainer->RetainerId) || !names.Add(name)) return false;
            result.Add(new RetainerIdentity(retainer->RetainerId, name));
        }
        retainers = result;
        return true;
    }

    public bool TryGetRetainerPickerOrder(out IReadOnlyList<RetainerIdentity> retainers, out string error)
    {
        retainers = [];
        if (!TryReadRetainerPickerEntries(out var entries, out error)) return false;
        retainers = entries.Select(entry => entry.Retainer).ToArray();
        error = string.Empty;
        return true;
    }

    private bool TryReadRetainerPickerEntries(out List<RetainerPickerEntry> entries, out string error)
    {
        entries = [];
        var picker = (AtkUnitBase*)gameGui.GetAddonByName("RetainerList").Address;
        if (picker == null || !picker->IsReady || !picker->IsVisible)
        { error = "The retainer picker is not ready."; return false; }
        if (!TryGetOwnRetainers(out var roster))
        { error = "The retainer ownership list could not be read safely."; return false; }

        for (var index = 0; index < roster.Count; index++)
        {
            var valueIndex = 3 + index * 10;
            if (picker->AtkValues == null || valueIndex + 8 >= picker->AtkValuesCount)
            { error = "The retainer picker's visible rows could not be verified."; return false; }
            ref var nameValue = ref picker->AtkValues[valueIndex];
            if (nameValue.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString) ||
                nameValue.String.Value == null)
            { error = "The retainer picker's row names are unavailable."; return false; }
            var name = MemoryHelper.ReadSeStringNullTerminated((nint)nameValue.String.Value).TextValue;
            var matches = roster.Where(retainer => retainer.Name == name).ToArray();
            if (matches.Length != 1)
            { error = "A retainer picker row could not be matched uniquely to your roster."; return false; }
            ref var activeValue = ref picker->AtkValues[valueIndex + 8];
            if (activeValue.Type != AtkValueType.Bool)
            { error = "The game's retainer availability flag could not be verified."; return false; }
            entries.Add(new RetainerPickerEntry(matches[0], activeValue.Bool));
        }
        if (entries.Count != roster.Count)
        { error = "The retainer picker did not show the complete retainer roster."; return false; }
        error = string.Empty;
        return true;
    }
    public bool TryCloseRetainerSellList(MarketSession expected, out string error)
    {
        if (IsSessionIdentityChanged(expected))
        { error = "The active retainer changed before the sale list could be closed."; return false; }
        if (GetSellAddon() != null || IsComparisonVisible)
        { error = "Close the item price and comparison windows before moving to another retainer."; return false; }
        var list = (AtkUnitBase*)gameGui.GetAddonByName("RetainerSellList").Address;
        if (list == null || !list->IsReady || !list->IsVisible)
        { error = "The current retainer's selling list is no longer open."; return false; }
        if (!list->Close(true))
        { error = "The game did not close the current retainer's selling list."; return false; }
        error = string.Empty;
        return true;
    }

    public bool TrySelectRetainerById(RetainerIdentity expected, out bool unavailable, out string error)
    {
        unavailable = false;
        if (!TryReadRetainerPickerEntries(out var entries, out error)) return false;

        var rowIndex = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Retainer.RetainerId != expected.RetainerId || entry.Retainer.Name != expected.Name) continue;
            if (rowIndex != -1)
            { error = "More than one retainer row matches the queued identity; selection was stopped safely."; return false; }
            rowIndex = index;
            if (!entry.IsAvailable)
            {
                unavailable = true;
                error = string.Empty;
                return false;
            }
        }
        if (rowIndex < 0)
        { error = "The queued retainer was not found in the visible picker."; return false; }

        var callback = stackalloc AtkValue[4];
        callback[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
        callback[1] = new AtkValue { Type = AtkValueType.UInt, UInt = (uint)rowIndex };
        callback[2] = default;
        callback[3] = default;
        var picker = (AtkUnitBase*)gameGui.GetAddonByName("RetainerList").Address;
        if (picker == null || !picker->IsReady || !picker->IsVisible)
        { error = "The retainer picker closed before selection."; return false; }
        picker->FireCallback(4, callback, true);
        error = string.Empty;
        return true;
    }
    public bool TrySelectRetainerMenuEntry(Func<string, bool> match, out string selectedText, out string error)
    {
        selectedText = string.Empty;
        var menu = (AddonSelectString*)gameGui.GetAddonByName("SelectString").Address;
        if (menu == null || !menu->IsReady || !menu->IsVisible)
        { error = "The retainer option menu is not open."; return false; }
        var popup = menu->PopupMenu.PopupMenu;
        if (popup.EntryNames == null || popup.EntryCount is <= 0 or > 64)
        { error = "The retainer option menu entries could not be read safely."; return false; }
        var matchIndex = -1;
        for (var index = 0; index < popup.EntryCount; index++)
        {
            var name = popup.EntryNames[index].Value;
            if (name == null) continue;
            var text = MemoryHelper.ReadSeStringNullTerminated((nint)name).TextValue;
            if (!match(text)) continue;
            if (matchIndex >= 0)
            { error = "The retainer option menu contains multiple matching entries; no choice was made."; return false; }
            matchIndex = index;
            selectedText = text;
        }
        if (matchIndex < 0)
        { error = "The expected retainer option was not found in the menu."; return false; }
        var callback = stackalloc AtkValue[1];
        callback[0] = new AtkValue { Type = AtkValueType.Int, Int = matchIndex };
        menu->FireCallback(1, callback, true);
        error = string.Empty;
        return true;
    }

    public bool TryAdvanceRetainerDialogue(RetainerIdentity expected, ulong previouslySelectedRetainerId, out string error)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName("Talk").Address;
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        { error = "The retainer dialogue is no longer open."; return false; }
        if (IsRetainerPickerVisible)
        { error = "The retainer picker is still open; the greeting was not advanced."; return false; }
        // RetainerManager.LastSelectedRetainerId can remain unset or refer to the previous
        // retainer while the Talk greeting is displayed. The selection callback has already
        // targeted `expected`; allow that transition state, but reject any other resolved ID.
        if (TryGetSelectedRetainerId(out var selectedId) && selectedId != expected.RetainerId &&
            selectedId != previouslySelectedRetainerId)
        { error = "The selected retainer no longer matches the queued retainer."; return false; }

        var stage = AtkStage.Instance();
        if (stage == null)
        { error = "The game's dialogue input handler is not available."; return false; }

        // Advance only the greeting for the retainer Auto update just selected.
        var click = stackalloc AtkEvent[1];
        click[0] = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &stage->AtkEventTarget,
            State = new() { StateFlags = (AtkEventStateFlags)132 }
        };
        var data = stackalloc AtkEventData[1];
        data[0] = default;
        addon->ReceiveEvent(AtkEventType.MouseDown, 0, click, data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, click, data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, click, data);
        error = string.Empty;
        return true;
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

    public bool TrySetNewListingQuantity(SellItem expected, uint quantity, out SellItem updated, out string error)
    {
        updated = null!;
        if (expected.IsExisting || expected.DialogGeneration == 0 || quantity == 0 || quantity > expected.Quantity)
        { error = "Choose a valid quantity for a new inventory listing."; return false; }
        if (!MatchesCurrentDialog(expected, true, out _, out error)) return false;
        var addon = GetSellAddon();
        var agent = AgentRetainer.Instance();
        var agentView = RetainerAgentView.For(agent);
        if (addon == null || agentView == null || addon->Quantity == null)
        { error = "The game's sale quantity field is unavailable."; return false; }

        // Use the game's numeric-input callback so its retainer agent receives the quantity too.
        addon->Quantity->InnerSetValue((int)quantity, true, false);
        if (addon->Quantity->Value != quantity || agentView->SellQuantity != quantity)
        { error = "The game's sale quantity field did not accept the batch size. No listing was confirmed."; return false; }
        if (!TryReadSellItem(out updated, out error)) return false;
        if (updated.ItemId != expected.ItemId || updated.IsHq != expected.IsHq || updated.Slot != expected.Slot ||
            updated.InventoryType != expected.InventoryType || updated.Quantity != quantity)
        { error = "The sale item changed while its batch size was being set. No listing was confirmed."; return false; }
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
        var agent = AgentRetainer.Instance();
        if (RetainerAgentView.For(agent) == null || !agent->IsAgentActive())
        { error = "The active retainer changed. Reopen its sale list and retry."; return false; }
        return OpenRetainerSell(agent, InventoryType.RetainerMarket, checked((ushort)expected.Slot), out error);
    }

    public bool TryOpenInventoryItem(CarriedItemCandidate expected, out string error)
    {
        if (!TryGetSession(out _, out error)) return false;
        if (GetSellAddon() != null || IsAddonReady("ItemSearchResult"))
        {
            error = "Close the current price or comparison window before opening another item.";
            return false;
        }
        var type = (InventoryType)expected.InventoryType;
        if (type is not (InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4) ||
            !TryGetStock(type, expected.Slot, out var stock, out error)) return false;
        if (stock->GetBaseItemId() != expected.ItemId || stock->IsHighQuality() != expected.IsHq ||
            stock->GetQuantity() != expected.Quantity)
        {
            error = "That inventory slot changed after the snapshot. Take a fresh snapshot before opening it.";
            return false;
        }
        if (IsBound(stock))
        {
            error = $"{expected.Name} is bound and cannot be listed. It was skipped.";
            return false;
        }
        var agent = AgentRetainer.Instance();
        if (RetainerAgentView.For(agent) == null || !agent->IsAgentActive())
        { error = "The active retainer changed. Reopen its sale list and retry."; return false; }
        return OpenRetainerSell(agent, type, checked((ushort)expected.Slot), out error);
    }

    public bool IsVendorShopOpen => GetVendorShopAddon() != null;
    public bool IsVendorContextMenuOpen => IsAddonVisible("ContextMenu");
    public bool IsVendorQuantityPromptOpen => IsAddonVisible("InputNumeric");
    public bool IsVendorConfirmationOpen => IsAddonVisible("SelectYesno");

    public bool TryDismissVendorItemContextMenu(out string error)
    {
        var shop = GetVendorShopAddon();
        var menu = (AddonContextMenu*)gameGui.GetAddonByName("ContextMenu").Address;
        var context = AgentInventoryContext.Instance();
        if (shop == null || menu == null || !menu->IsReady || !menu->IsVisible || context == null ||
            context->OwnerAddonId != shop->Id)
        {
            error = "An unrelated item menu is open. Close it before Auto vendor can continue.";
            return false;
        }

        // Auto vendor was explicitly started, and this item menu belongs to the active shop.
        // Closing it does not select an action or sell anything.
        menu->Close(true);
        error = string.Empty;
        return true;
    }

    public bool TrySellInventoryItemToVendor(CarriedItemCandidate expected, out string error)
    {
        error = string.Empty;
        var shop = GetVendorShopAddon();
        if (shop == null)
        { error = "Open an NPC vendor's Shop window before starting Auto vendor."; return false; }
        if (IsVendorContextMenuOpen || IsVendorQuantityPromptOpen || IsVendorConfirmationOpen)
        { error = "Close the current vendor item menu or prompt before starting Auto vendor."; return false; }
        if (sellItemToVendor is null)
        { error = VendorSaleAvailabilityError ?? "The game's NPC vendor sale action is unavailable on this client build."; return false; }
        var type = (InventoryType)expected.InventoryType;
        if (type is not (InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4))
        { error = "The selected item is not in carried inventory."; return false; }
        if (!TryGetStock(type, expected.Slot, out var stock, out error)) return false;
        if (stock->GetBaseItemId() != expected.ItemId || stock->IsHighQuality() != expected.IsHq ||
            stock->GetQuantity() != expected.Quantity)
        { error = "The inventory item changed before it could be sold. The vendor run stopped safely."; return false; }
        if (IsBound(stock))
        { error = $"{expected.Name} is bound and cannot be sold. It was skipped."; return false; }
        try
        {
            // The game applies the vendor sale to this exact inventory slot; verify the stack
            // before and after because this action can sell immediately without a dialog.
            sellItemToVendor((uint)expected.Slot, type, 0);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "The game's NPC vendor sale action failed. No next item was attempted.";
            log.Warning(ex, "Calling the NPC vendor sale action failed for {ItemName} in slot {Slot}.", expected.Name, expected.Slot);
            return false;
        }
    }

    public bool TryGetCarriedItemTotal(uint itemId, bool isHq, out uint quantity, out string error)
    {
        quantity = 0;
        var inventory = InventoryManager.Instance();
        if (inventory == null) { error = "The carried inventory is not available."; return false; }
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2,
                     InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            { error = "The carried inventory is still updating."; return false; }
            for (var slot = 0; slot < container->Size; slot++)
            {
                var stock = container->GetInventorySlot(slot);
                if (stock == null || stock->IsEmpty() || stock->GetBaseItemId() != itemId || stock->IsHighQuality() != isHq)
                    continue;
                quantity = checked(quantity + stock->GetQuantity());
            }
        }
        error = string.Empty;
        return true;
    }

    private bool OpenRetainerSell(AgentRetainer* agent, InventoryType inventoryType, ushort slot, out string error)
    {
        if (openRetainerSell is null)
        {
            error = ItemSelectorAvailabilityError ?? "The game's retainer item selector is unavailable.";
            return false;
        }
        try
        {
            openRetainerSell(agent, inventoryType, slot);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "The retainer item selector failed on this client build. No listing was changed.";
            log.Warning(ex, "Opening a retainer listing item failed.");
            return false;
        }
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

    public bool TryConfirmNewListing(SellItem expected, uint price, out string error)
    {
        if (expected.IsExisting || expected.DialogGeneration == 0)
        {
            error = "Open a new inventory item for sale before confirming a listing.";
            return false;
        }
        if (!TryFillPrice(expected, price, out error)) return false;
        if (!MatchesCurrentDialog(expected with { CurrentPrice = price }, true, out _, out error)) return false;
        var addon = GetSellAddon();
        if (addon == null) { error = "The sell window closed before the listing could be confirmed."; return false; }
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
        if (proxy == null || addon == null)
        {
            error = "The marketboard search is not available.";
            return false;
        }
        if (proxy->WaitingForListings)
        {
            error = "The previous marketboard search is still finishing.";
            return false;
        }
        if (IsComparisonVisible)
        {
            error = "A market comparison is already open. Close it before continuing.";
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
        // EndRequest normally captures this, but some client builds expose the completed proxy state
        // to the framework update before the hooked callback runs. Recover from that state rather than
        // leaving the caller waiting until its timeout.
        var proxy = InfoProxyItemSearch.Instance();
        if (localSnapshot == null && resultReceived && proxy != null && !proxy->WaitingForListings)
            CaptureLocalSnapshot(proxy);
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
        if (comparison == null || !comparison->IsReady || !comparison->IsVisible) { error = string.Empty; return true; }
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

    private AddonShop* GetVendorShopAddon()
    {
        var addon = (AddonShop*)gameGui.GetAddonByName("Shop").Address;
        return addon != null && addon->IsReady && addon->IsVisible ? addon : null;
    }

    private bool IsAddonReady(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsReady;
    }

    private bool IsAddonVisible(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsReady && addon->IsVisible;
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

    private static bool IsBound(InventoryItem* stock) =>
        stock != null && stock->GetSpiritbondOrCollectability() != 0;

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
        try
        {
            // Capture before the game's EndRequest handler gets a chance to reset its proxy state.
            CaptureLocalSnapshot(proxy);
        }
        catch (Exception ex)
        {
            MarkLocalCaptureFailure(ex);
        }

        // On some builds EndRequest itself clears WaitingForListings. If the pre-call capture saw
        // that transient state, retry after the original handler has finalized the proxy.
        endHook!.Original(proxy);
        if (localSnapshot == null && resultReceived)
        {
            try { CaptureLocalSnapshot(proxy); }
            catch (Exception ex) { MarkLocalCaptureFailure(ex); }
        }
    }

    private void MarkLocalCaptureFailure(Exception ex)
    {
        localSnapshot = null;
        localError = "The local response could not be verified. Request prices again.";
        requestComplete = true;
        log.Error(ex, "Copying complete market response failed.");
    }

    private void CaptureLocalSnapshot(InfoProxyItemSearch* proxy)
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
