# Retainer Pricer

Dalamud API 15 development plugin for pricing items in the retainer sale window.

## Use

1. Open the plugin with `/retainerpricer` from the main menu or while viewing a retainer's selling list.
2. The price-source setting applies to individual checks. Both automatic buttons always use a fresh local marketboard comparison.
3. Open a retainer's sale list and click **Start listing items**. It processes eligible carried inventory automatically: skips untradeable, nonmarketable and excluded items, checks each price from the live local marketboard, sets one gil below the lowest matching listing, and confirms each new sale. No price review or item-name entry is required. It stops when eligible inventory is done, the retainer's 20 slots are full, or you press **Stop**. If it cannot verify that a sale reached the retainer list, it stops before trying another item.
4. To check a price without listing, use **Price lookup** and retrieve from a captured inventory or retainer-list row. For a single manually opened sale window, **Automatically price new listings** still fills the price; confirm that individual sale in game.
5. To automatically reprice current stock, click **Update existing listings**. It checks each eligible listing against a fresh local marketboard response, sets the price one gil below the lowest matching listing, submits the update, and verifies it before moving to the next item. No review/apply step is needed. Each row shows the result; use **Exclude** to add an item to the persistent exception list.

Prices compare HQ and NQ separately, exclude all of your retainers, and target one gil below the lowest usable competing listing. Inventory snapshots read the four carried inventory bags; untradeable items and items without a marketboard search category are skipped automatically. The optional maximum-age filter is off by default and only applies to Universalis lookups. Add or remove persistent item exceptions under **Settings** or directly beside a captured item; excluded items are skipped by both automatic actions, while manual lookups remain available. Automatic listing and repricing use the game's live local comparison and registered Confirm action. An item is left unchanged when no safe matching market price can be calculated.

Universalis requests are on demand and are limited to the selected item and home world. The plugin does not upload player or market data.

## Build

```powershell
dotnet build RetainerPricer.csproj --configuration Release
dotnet run --project checks\RetainerPricer.Checks.csproj --configuration Release
```

The Release build places the plugin DLL at `dist\Release\RetainerPricer.dll` and its install package in `dist\Release\RetainerPricer`. GitHub Actions builds pushes and pull requests, then creates or updates the release package and refreshes the Dalamud feed for a published `vX.Y.Z` release or pushed tag.

## Development install

In Dalamud, open Settings → Experimental → Dev Plugin Locations and add the full path to `dist\Release\RetainerPricer.dll`. Then open the Plugin Installer → Dev Tools → Installed Dev Plugins and enable Retainer Pricer. Add the path once; later Release builds replace the DLL at the same path. See Dalamud's [development FAQ](https://dalamud.dev/faq/getting-started/).

After the first public release, add `https://raw.githubusercontent.com/Jacob5800/RetainerPricer/main/repo.json` under Dalamud Settings → Experimental → Custom Plugin Repositories for installer updates.

## Compatibility and verification

Pricing logic and the Universalis client have standalone synthetic-data checks in `checks`. They do not test native Dalamud callbacks or interact with the game. The retainer UI bridge uses public FFXIVClientStructs definitions and a version-gated compatibility view for fields absent in some API 15 SDK snapshots. It disables retainer operations if the verified `AgentRetainer` size does not match. The listing selector accepts repeated official-signature call sites only when every match resolves to the same target within the game text section; if it cannot be verified, scanning stays available and applying updates is disabled with an explanation.

Native callback timing, client-version compatibility and live listing update behavior still require manual validation in a supported game build. If the retainer item selector cannot be verified, price scanning remains available and the plugin explains why applying updates is disabled.
