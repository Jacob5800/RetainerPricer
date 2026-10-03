# Retainer Pricer release rules

- For every release, update `Changelog` in `RetainerPricer.json` with concise, user-facing notes for that version.
- Keep `scripts/Update-RepoManifest.ps1` copying `Changelog` into the custom `repo.json` entry. The script rejects releases with no changelog.
- Before publishing, verify that the packaged plugin manifest and generated repository feed both contain the changelog and the same assembly version.
