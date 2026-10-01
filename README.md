# Retainer Pricer

Dalamud API 15 development plugin for pricing items in the retainer sale window.

## Use

1. Open the plugin with `/retainerpricer` while viewing a retainer's selling list.
2. Choose **Universalis** for a same-world community price snapshot or **Local marketboard** to request a fresh in-game comparison.
3. For a new listing, leave **Automatically price new listings** enabled, then open the item's sale window. The plugin fills the price field; confirm the listing with the game's own button.
4. For existing listings, keep the sale list open, choose **Update existing listings**, review the proposed prices and selected rows, then choose **Apply selected updates**.

Prices are per item, compare HQ and NQ separately, exclude listings from your retainers, and target one gil below the lowest usable competing listing. Minimum price and maximum age settings are available in the plugin. Existing listing updates are checked again immediately before submission. Each accepted update uses the game's own sale window and registered Confirm action; it is not submitted as an unattended background market action.

Universalis requests are on demand and are limited to the selected item and home world. The plugin does not upload player or market data.

## Build

```powershell
dotnet build RetainerPricer.csproj --configuration Release
dotnet run --project checks\RetainerPricer.Checks.csproj --configuration Release
```

The Release build places the plugin DLL at `dist\Release\RetainerPricer.dll` and its install package in `dist\Release\RetainerPricer`. GitHub Actions builds pushes and pull requests, then creates a GitHub Release and refreshes the Dalamud feed when a `vX.Y.Z` tag is pushed.

## Development install

In Dalamud, open Settings → Experimental → Dev Plugin Locations and add the full path to `dist\Release\RetainerPricer.dll`. Then open the Plugin Installer → Dev Tools → Installed Dev Plugins and enable Retainer Pricer. Add the path once; later Release builds replace the DLL at the same path. See Dalamud's [development FAQ](https://dalamud.dev/faq/getting-started/).

After the first public release, add `https://raw.githubusercontent.com/Jacob5800/RetainerPricer/main/repo.json` under Dalamud Settings → Experimental → Custom Plugin Repositories for installer updates.

## Compatibility and verification

Pricing logic and the Universalis client have standalone synthetic-data checks in `checks`. They do not test native Dalamud callbacks or interact with the game. The retainer UI bridge uses public FFXIVClientStructs definitions and a version-gated compatibility view for fields absent in some API 15 SDK snapshots. It disables retainer operations if the verified `AgentRetainer` size does not match. The game's native listing selector is only used when its signature resolves uniquely and is verified in the game text section; if not, existing-listing batch updates are disabled.

Native callback timing, client-version compatibility and live listing update behavior still require manual validation in a supported game build.
