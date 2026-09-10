# Version update checklist

When changing the application version, update and verify the following locations.

- `src/AltHenkan/AltHenkan.csproj`: `Version` (the installer reads it from here; `installer.iss` needs no change)
- `README.md`: documented version, if one is shown in the future
- Release notes or changelog, if added in the future

