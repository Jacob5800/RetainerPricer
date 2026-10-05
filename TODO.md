# TODO

## v0.4.8 — Saved gear-set protection

Implemented:

- Automatically protect items used by saved gear sets from automatic listing, existing-listing repricing, Auto update, and Auto vendor. The protection refreshes from the current character's saved gear sets; automation pauses if that data is unavailable.
- Updated the `?` help, README, plugin manifest, and release changelog.

Pending in-game observation:

- Verify a saved gear-set item is skipped by automatic listing, repricing, Auto update, and Auto vendor, and becomes eligible after removing it from all gear sets unless it remains in Exceptions.

## v0.4.7 — Retainer greeting, vendor action, and Sniper wording

Implemented:

- Auto update advances the greeting after selecting a retainer even while the game's selected-retainer ID is temporarily unset or still reports the previously selected retainer. It retries while the greeting remains open, while rejecting any other resolved retainer ID.
- Auto vendor uses the game's vendor sale action for a revalidated inventory slot, then confirms the expected stack was removed before continuing.
- Sniper shows the deal threshold as a percentage of median (91.0% default) instead of a decimal multiplier.
- Updated the `?` help, README, plugin manifest, and changelog for the new behavior.

Pending in-game observation:

- Confirm Auto update advances the “I have come, Master” greeting and continues into the retainer options and listing screens.
- Confirm Auto vendor sells a qualifying stack and proceeds to the next candidate after inventory verification.

## v0.4.6 — Inventory binding and vendor menu handling

Implemented:

- Automatically omit spiritbound equipment from market-listing inventory snapshots and recheck binding before opening or repricing a sale window.
- Safely close an already-open item menu owned by the active vendor before continuing.
- Add `/retainer` as an alternate command for opening the plugin.
- Update the `?` help, README, plugin manifest, and changelog for the new behavior.

Pending in-game observation:

- Confirm spiritbound gear is omitted from automatic listing while unbound copies remain eligible.
- Confirm Auto vendor safely handles a vendor-owned context menu before continuing.
- Confirm `/retainer` opens the plugin without conflicting with another installed command.

## v0.4.3 — Sniper batch pacing and large-drop review

Implemented:

- Sniper history queries send up to 100 item IDs in each Universalis request and space consecutive history batches at least one second apart.
- Added a 3–14-day selectable history window and updated the Sniper UI, help, README, manifest, and repo feed changelog.
- Existing-listing and Auto update runs hold proposed reprices more than 50% below the current listing price; after each retainer scan, the user can approve a fresh price check or ignore the item.
- Added standalone coverage for 100-item query construction, response parsing, and one-second batch spacing.
- A live read-only 100-ID request to Alpha (world 402) returned the batch response shape and all 100 submitted IDs; the sample included 82 unresolved IDs, so it validates the batch endpoint shape, not 100 live market histories.

Pending in-game observation:

- Confirm normal repricing continues past a held large-drop item, the popup offers both choices, and approval fetches a fresh quote before applying.
- Confirm Auto update pauses for the review before it navigates to the next retainer.
- Confirm Sniper scans real marketable items with at least one second between successive 100-item batches, then opens the live feed.

## v0.4.1 — Per-item repricing protection

Implemented:

- Added a persistent `Don't reprice` list separate from global exceptions.
- Items on the new list are omitted from existing-listing repricing and Auto update, but remain eligible for new listings.
- Added quick protection actions beside captured and reviewed existing listings.
- Updated the `?` help, README, plugin manifest, and repository feed changelog.

Pending in-game observation:

- Confirm a protected item is skipped by both repricing actions while an unprotected item still updates normally.
- Confirm new listing automation still accepts an item that is only on `Don't reprice`.

## v0.4.0 — Universalis Sniper

Implemented:

- Replaced the hand-maintained watchlist with a scan of every marketable item on the home world.
- Batches up to 100 item histories in one Universalis request, with at least one second between Sniper history-batch requests.
- Added a configurable 3–14-day sales window and calculates separate HQ/NQ median unit prices using up to 1,800 recent sales per item.
- Flags new 1-gil listings across marketable items for manual review; the plugin never purchases automatically.
- Updated the Sniper UI, `?` help tab, README, plugin manifest, and repository feed changelog.

Pending in-game observation:

- Confirm the WebSocket connects and detects a newly listed watched item on the home world; confirm listing removals clear corresponding deal rows.
- Confirm the history baseline and HQ/NQ filters with an item that has recent sales of both qualities.

## v0.3.1 — Auto update from the picker

Implemented:

- Auto update now starts either from the retainer picker in its displayed top-to-bottom order, or from an open selling list with that retainer first.
- The retainer menu Quit match tolerates trailing punctuation and waits for the picker to fully return before advancing.
- Picker rows are matched against the owned-retainer roster and availability flag before selecting; unavailable retainers are skipped.
- New listings cap each sale at 99 and verify/continue the remainder from the same inventory stack.
- Removed the extra fixed pauses after repricing, listing, and retainer-menu actions; the 250 ms state polling remains.
- Updated the in-plugin ? help, README, manifest changelog, and release reminder for help maintenance.

Pending in-game observation:

- Replay Auto update starting from the Retainer selection screen and verify it processes several retainers in visible top-to-bottom order.
- Replay with a carried stack above 99 and confirm it creates 99-item listings followed by the final remainder, subject to free market slots.
