using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Item = Lumina.Excel.Sheets.Item;
using World = Lumina.Excel.Sheets.World;

namespace RetainerPricer;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IDtrBar dtrBar;
    private readonly WindowSystem windows = new("RetainerPricer");
    private readonly PluginConfig config;
    private readonly NativeMarketBridge bridge;
    private readonly UniversalisClient universalis = new();
    private readonly FeedbackClient feedback = new();
    private readonly PricingController controller;
    private readonly SniperMonitor sniper;
    private readonly AutoVendorController vendor;
    private readonly VentureController ventures;
    private readonly MainWindow window;
    private IDtrBarEntry? dtrBarEntry;
    private bool wasOpen;
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        IGameGui gameGui, IDataManager data, IPlayerState player, IClientState clientState,
        ICondition condition, IAddonLifecycle addons, IDtrBar dtrBar,
        IGameInteropProvider interop, ISigScanner sigScanner, IPluginLog log)
    {
        (this.pluginInterface, this.commands, this.framework, this.log) = (pluginInterface, commands, framework, log);
        this.dtrBar = dtrBar;
        config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        var migrateConfig = config.Version < 9;
        if (config.Version < 8)
        {
            // Move users from the former 0.10 default while preserving any custom threshold.
            if (Math.Abs(config.SniperThresholdFraction - 0.10) < 0.000001)
                config.SniperThresholdFraction = 0.910;
        }
        if (migrateConfig) config.Version = 9;
        config.Normalize();
        if (migrateConfig) pluginInterface.SavePluginConfig(config);
        bridge = new NativeMarketBridge(gameGui, data, player, clientState, condition, addons, interop, sigScanner, log);
        var itemSheet = data.GetExcelSheet<Item>();
        var itemChoices = itemSheet
            .Select(item => new ItemChoice(item.RowId, item.Name.ToString()))
            .Where(item => item.ItemId != 0 && !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var marketableItemIds = itemSheet
            .Where(item => item.RowId != 0 && !item.IsUntradable && item.ItemSearchCategory.RowId != 0)
            .Select(item => item.RowId)
            .ToHashSet();
        var marketableItemChoices = itemChoices.Where(item => marketableItemIds.Contains(item.ItemId)).ToArray();
        var worldNames = data.GetExcelSheet<World>()
            .Where(world => world.RowId != 0)
            .GroupBy(world => world.RowId)
            .ToDictionary(group => group.Key, group => group.First().Name.ToString());
        controller = new PricingController(bridge, universalis, config, marketableItemIds);
        sniper = new SniperMonitor(universalis, config, marketableItemChoices, worldNames);
        vendor = new AutoVendorController(bridge, universalis, config, marketableItemIds);
        ventures = new VentureController(bridge, config);
        window = new MainWindow(config, controller, itemChoices, bridge.GetHomeWorld, Save, Dispatch, UpdateServerInfoBarButton,
            () => bridge.RetainerAvailabilityError, feedback, sniper, vendor, ventures);
        windows.AddWindow(window);
        UpdateServerInfoBarButton();
        if (bridge.LocalAvailabilityError is { } localCompatibilityError)
            log.Warning("Retainer Pricer local pricing: {Error}", localCompatibilityError);
        if (bridge.RetainerAvailabilityError is { } retainerCompatibilityError)
            log.Warning("Retainer Pricer: {Error}", retainerCompatibilityError);
        if (bridge.ItemSelectorAvailabilityError is { } selectorCompatibilityError)
            log.Warning("Retainer Pricer: {Error}", selectorCompatibilityError);
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += Toggle;
        framework.Update += Update;
        commands.AddHandler("/retainerpricer", new CommandInfo((_, _) => Toggle())
        { HelpMessage = "Open Retainer Pricer: automatically price new listings and review existing listing updates." });
        commands.AddHandler("/retainer", new CommandInfo((_, _) => Toggle())
        { HelpMessage = "Open Retainer Pricer." });
    }

    private void Dispatch(Action action)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (disposed) return;
            try { action(); }
            catch (Exception ex)
            {
                log.Error(ex, "Retainer Pricer operation failed");
                controller.Cancel("The operation failed. No further prices will be submitted; see Dalamud's log.");
                vendor.Cancel("Auto vendor stopped after an unexpected error. Check the vendor window before continuing.");
                ventures.Cancel("Venture cycle stopped after an unexpected error. Check the current retainer window before continuing.");
            }
        });
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        try
        {
            controller.Update();
            vendor.Update();
            ventures.Update();
            var open = controller.HasRetainer;
            if (config.OpenWithRetainer && open && !wasOpen) window.IsOpen = true;
            wasOpen = open;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Retainer Pricer stopped after an unexpected error");
            controller.Cancel("Pricing stopped after an unexpected error. Reopen the plugin to check its status.");
            vendor.Cancel("Auto vendor stopped after an unexpected error. Check the vendor window before continuing.");
            ventures.Cancel("Venture cycle stopped after an unexpected error. Check the current retainer window before continuing.");
        }
    }

    private void Toggle() => window.Toggle();
    private void Save() => pluginInterface.SavePluginConfig(config);

    private void UpdateServerInfoBarButton()
    {
        if (config.ShowServerInfoBarButton)
        {
            dtrBarEntry ??= dtrBar.Get("Retainer Pricer");
            var buttonText = new SeStringBuilder();
            buttonText.AddText("RP");
            dtrBarEntry.Text = buttonText.Build();
            var tooltip = new SeStringBuilder();
            tooltip.AddText("Open Retainer Pricer");
            dtrBarEntry.Tooltip = tooltip.Build();
            dtrBarEntry.OnClick = _ => window.IsOpen = true;
        }
        else
        {
            dtrBarEntry?.Remove();
            dtrBarEntry = null;
        }
    }

    public void Dispose()
    {
        disposed = true;
        framework.Update -= Update;
        dtrBarEntry?.Remove();
        dtrBarEntry = null;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= Toggle;
        pluginInterface.UiBuilder.OpenConfigUi -= Toggle;
        commands.RemoveHandler("/retainerpricer");
        commands.RemoveHandler("/retainer");
        controller.Dispose();
        vendor.Dispose();
        sniper.Dispose();
        bridge.Dispose();
        universalis.Dispose();
        feedback.Dispose();
        windows.RemoveAllWindows();
    }
}
