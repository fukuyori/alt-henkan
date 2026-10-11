# Version update checklist

When changing the application version, update and verify the following locations.

- `src/AltHenkan/AltHenkan.csproj`: `Version` (the installer reads it from here; `installer.iss` needs no change)
- `README.md`: source version at the top and the summary of changes for that version
- `CHANGELOG.md`: add the new version section and summarize its changes
- Release notes, if maintained separately in the future

Verify the version flow after editing:

- `scripts/build-installer.ps1` reads the project version and passes `/DMyAppVersion` to `installer.iss`. Keep this single source of truth.
- Build with `dotnet build .\src\AltHenkan\AltHenkan.csproj --configuration Release --no-restore` and inspect `src/AltHenkan/bin/Release/net8.0-windows/AltHenkan.dll` for the expected assembly, file, and product versions.
- Diagnostic session headers read the assembly version automatically; the input trace `schema` is a separate format version and does not change solely because the application version changes.
- Keep historical versions in `CHANGELOG.md` intact. Move the applicable unreleased changes into the new version section.
- Run `git diff --check`. Building an installer, signing, committing, tagging, and publishing are separate operations.
