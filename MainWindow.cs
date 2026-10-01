using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RetainerPricer;

internal sealed class MainWindow : Window
{
    private readonly PluginConfig config;
    private readonly PricingController controller;
    private readonly Action save;
    private readonly Action<Action> dispatch;
    private readonly Func<string?> localError;
    private readonly Func<string?> retainerError;

    public MainWindow(PluginConfig config, PricingController controller, Action save, Action<Action> dispatch,
        Func<string?> localError, Func<string?> retainerError) : base("Retainer Pricer")
    {
        (this.config, this.controller, this.save, this.dispatch, this.localError, this.retainerError) =
            (config, controller, save, dispatch, localError, retainerError);
        Size = new Vector2(860, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("One gil below the lowest matching listing on your home world. HQ and NQ are compared separately; your own retainers are excluded.");
        if (retainerError() is { } nativeError) ImGui.TextWrapped(nativeError);
        ImGui.BeginDisabled(controller.Busy);
        var automatic = config.AutoPriceNewListings;
        if (ImGui.Checkbox("Automatically price new listings", ref automatic)) { config.AutoPriceNewListings = automatic; save(); }
        ImGui.TextDisabled("Open an item for sale: its price is filled automatically. Confirm the new sale in the game.");
        var source = (int)config.Source;
        ImGui.SetNextItemWidth(250);
        if (ImGui.Combo("Price source", ref source, "Universalis\0Local marketboard\0"))
        { config.Source = (PriceSource)source; save(); }
        if (config.Source == PriceSource.Universalis)
            ImGui.TextWrapped("Universalis contains prices uploaded by players. Old or incomplete results will be skipped; switch to Local for a fresh game check.");
        else
        {
            ImGui.TextWrapped("Local opens Compare Prices for the item and waits for the complete marketboard response. Batch checks open one listing at a time.");
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
            ImGui.TextWrapped("Open your retainer's selling list, then choose an item to sell or adjust an existing listing's price. Retainer Pricer will identify the item and quantity.");
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
        ImGui.TextWrapped("Keep the current retainer's selling list open. Check prices, review the changes below, then apply the selected rows.");
        if (controller.ExistingUpdateError is { } updateError) ImGui.TextWrapped(updateError);
        ImGui.BeginDisabled(controller.Busy || !controller.CanUpdateExisting);
        if (ImGui.Button("Update existing listings")) dispatch(controller.ScanExisting);
        if (controller.Rows.Count > 0)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(!controller.CanUpdateExisting);
            if (ImGui.Button("Apply selected updates")) dispatch(controller.ApplyReviewed);
            ImGui.EndDisabled();
        }
        ImGui.EndDisabled();
        if (controller.Rows.Count == 0) return;
        if (ImGui.BeginTable("##existingPrices", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.Resizable, new Vector2(0, Math.Max(150, ImGui.GetContentRegionAvail().Y - 30))))
        {
            ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 34);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 45);
            ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 74);
            ImGui.TableSetupColumn("Suggested", ImGuiTableColumnFlags.WidthFixed, 74);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Result");
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var row in controller.Rows)
            {
                ImGui.PushID(row.Item.Slot);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(controller.Busy || row.Proposal is not { CanApply: true });
                var selected = row.Selected;
                if (ImGui.Checkbox("##selected", ref selected)) row.Selected = selected;
                ImGui.EndDisabled();
                ImGui.TableNextColumn(); ImGui.TextWrapped(row.Item.Name + (row.Item.IsHq ? " (HQ)" : ""));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Item.Quantity.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Item.CurrentPrice.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Proposal is { CanApply: true } p ? p.SuggestedPrice.ToString("N0") : "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Snapshot?.Source.ToString() ?? "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(row.Snapshot is { } data ? Age(data.ObservedAt) : "—");
                ImGui.TableNextColumn(); ImGui.TextWrapped(row.Status);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
    }

    private void DrawSettings()
    {
        ImGui.BeginDisabled(controller.Busy);
        var lowerOnly = config.OnlyLowerExistingPrices;
        if (ImGui.Checkbox("Only lower existing prices", ref lowerOnly)) { config.OnlyLowerExistingPrices = lowerOnly; save(); }
        var minimum = config.MinimumPrice;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Minimum price per item (gil)", ref minimum))
        { config.MinimumPrice = minimum; config.Normalize(); save(); }
        var age = config.MaximumAgeMinutes;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Maximum price age (minutes)", ref age))
        { config.MaximumAgeMinutes = age; config.Normalize(); save(); }
        var open = config.OpenWithRetainer;
        if (ImGui.Checkbox("Open this window with the retainer selling list", ref open)) { config.OpenWithRetainer = open; save(); }
        ImGui.EndDisabled();
        ImGui.TextWrapped("Prices are per item, before tax. If an undercut would be below your minimum, or no matching competitor is available, that item is left unchanged.");
        ImGui.TextWrapped("Closing the retainer or changing character/world stops a batch. Stop prevents further submissions; completed price changes stay applied.");
    }

    private static void DrawAge(PriceSnapshot snapshot)
        => ImGui.TextUnformatted($"{snapshot.Source}: {Age(snapshot.ObservedAt)} old · {snapshot.ObservedAt.ToLocalTime():HH:mm:ss}");

    private static string Age(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        return age.TotalMinutes >= 1 ? $"{Math.Max(0, (int)age.TotalMinutes)}m" : $"{Math.Max(0, (int)age.TotalSeconds)}s";
    }
}
