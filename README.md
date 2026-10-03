# Retainer Pricer

Dalamud API 15 plugin for pricing items in the retainer sale window.

## Use

1. Open the plugin with `/retainerpricer` from the main menu or while viewing a retainer's selling list.
2. Automatic new-item pricing and both batch actions use Universalis. A price is only suggested when there is a current competing listing of the same quality and a sale from the previous 20 days.
3. Open a retainer's sale list and click **Start listing items**. It processes eligible carried inventory automatically: skips untradeable, nonmarketable, excluded, or inactive items; checks Universalis; sets one gil below the lowest matching listing; and confirms each new sale. Items with no current price or no sale during the previous 20 days are skipped with an explanation. It stops when eligible inventory is done, the retainer's 20 slots are full, or you press **Stop**. If it cannot verify that a sale reached the retainer list, it stops before trying another item.
4. To check a price without listing, use **Price lookup** and retrieve from a captured inventory or retainer-list row. For a single manually opened sale window, **Automatically price new listings** still fills the price; confirm that individual sale in game.
5. To automatically reprice current stock, click **Update existing listings**. It checks each eligible listing against Universalis, sets the price one gil below the lowest matching listing, submits the update, and verifies it before moving to the next item. No review/apply step is needed. Rows show whether a price was applied or skipped for missing recent history or competition; use **Exclude** to add an item to the persistent exception list.

Prices compare HQ and NQ separately, exclude all of your retainers, and target one gil below the lowest usable competing listing. Inventory snapshots read the four carried inventory bags; untradeable items and items without a marketboard search category are skipped automatically. The optional maximum-age filter is off by default. Use the **Exceptions** tab to grab carried inventory, filter item names, select an item from the dropdown, and add or remove persistent exclusions. Excluded items are skipped by both automatic actions, while manual lookups remain available. The on-screen **Check price again** source can use the local marketboard. Automatic batches use Universalis; the batch waits for sale windows and listing updates to reach the expected state, and stops safely if the game UI does not.

Universalis requests are on demand and are limited to the selected item and home world. Items are left unchanged when Universalis has no matching current listing or no sale from the previous 20 days. The plugin does not upload player or market data.

## Build

```powershell
dotnet build RetainerPricer.csproj --configuration Release
```

The Release build places the plugin DLL at `dist\Release\RetainerPricer.dll` and its install package in `dist\Release\RetainerPricer`. GitHub Actions builds pushes and pull requests, then creates or updates the release package and refreshes the Dalamud feed for a published `vX.Y.Z` release or pushed tag.

## Install

In Dalamud Settings → Experimental → Custom Plugin Repositories, add `https://raw.githubusercontent.com/Jacob5800/RetainerPricer/main/repo.json` for installer updates.
