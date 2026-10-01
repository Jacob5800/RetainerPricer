using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Item = Lumina.Excel.Sheets.Item;

namespace RetainerPricer;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WindowSystem windows = new("RetainerPricer");
    private readonly PluginConfig config;
    private readonly NativeMarketBridge bridge;
    private readonly UniversalisClient universalis = new();
    private readonly PricingController controller;
    private readonly MainWindow window;
    private bool wasOpen;
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework,
        IGameGui gameGui, IDataManager data, IPlayerState player, IAddonLifecycle addons,
        IGameInteropProvider interop, ISigScanner sigScanner, IPluginLog log)
    {
        (this.pluginInterface, this.commands, this.framework, this.log) = (pluginInterface, commands, framework, log);
        config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        config.Normalize();
        bridge = new NativeMarketBridge(gameGui, data, player, addons, interop, sigScanner, log);
        controller = new PricingController(bridge, universalis, config);
        var itemChoices = data.GetExcelSheet<Item>()
            .Select(item => new ItemChoice(item.RowId, item.Name.ToString()))
            .Where(item => item.ItemId != 0 && !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        window = new MainWindow(config, controller, itemChoices, bridge.GetHomeWorld, Save, Dispatch,
            () => bridge.LocalAvailabilityError, () => bridge.RetainerAvailabilityError);
        windows.AddWindow(window);
        if (bridge.LocalAvailabilityError is { } localCompatibilityError)
            log.Warning("Retainer Pricer local pricing: {Error}", localCompatibilityError);
        if (bridge.RetainerAvailabilityError is { } retainerCompatibilityError)
            log.Warning("Retainer Pricer: {Error}", retainerCompatibilityError);
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += Toggle;
        framework.Update += Update;
        commands.AddHandler("/retainerpricer", new CommandInfo((_, _) => Toggle())
        { HelpMessage = "Open Retainer Pricer: automatically price new listings and review existing listing updates." });
    }

    private void Dispatch(Action action)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (disposed) return;
            try { action(); }
            catch (Exception ex) { log.Error(ex, "Retainer Pricer operation failed"); controller.Cancel("The operation failed. No further prices will be submitted; see Dalamud's log."); }
        });
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        try
        {
            controller.Update();
            var open = controller.HasRetainer;
            if (config.OpenWithRetainer && open && !wasOpen) window.IsOpen = true;
            wasOpen = open;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Retainer Pricer stopped after an unexpected error");
            controller.Cancel("Pricing stopped after an unexpected error. Reopen the plugin to check its status.");
        }
    }

    private void Toggle() => window.Toggle();
    private void Save() => pluginInterface.SavePluginConfig(config);

    public void Dispose()
    {
        disposed = true;
        framework.Update -= Update;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= Toggle;
        pluginInterface.UiBuilder.OpenConfigUi -= Toggle;
        commands.RemoveHandler("/retainerpricer");
        controller.Dispose();
        bridge.Dispose();
        universalis.Dispose();
        windows.RemoveAllWindows();
    }
}
