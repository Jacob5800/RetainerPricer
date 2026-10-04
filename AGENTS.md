# Retainer Pricer release rules

- For every release, update `Changelog` in `RetainerPricer.json` with concise, user-facing notes for that version.
- Keep `scripts/Update-RepoManifest.ps1` copying `Changelog` into the custom `repo.json` entry. The script rejects releases with no changelog.
- Before publishing, verify that the packaged plugin manifest and generated repository feed both contain the changelog and the same assembly version.
- On every release, bump the project and plugin manifest versions, and verify the small version label at the bottom-left of the plugin menu reports that release version.
- Keep the batch selling documentation in `README.md` aligned with batch controls, including bulk inventory add and the per-run total quantity cap.
- For every release, review and update the plugin's `?` tab in `MainWindow.DrawHelp()` so its instructions match the current buttons, tabs, settings, and behavior; verify the release version reminder as part of the menu review.
