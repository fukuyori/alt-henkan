# Version update checklist

When changing the application version, update and verify the following locations.

- `src/AltHenkan/AltHenkan.csproj`: `Version` (the installer reads it from here; `installer.iss` needs no change)
- `README.md`: documented version, if one is shown in the future
- `CHANGELOG.md`: add the new version section and summarize its changes
- Release notes, if maintained separately in the future
