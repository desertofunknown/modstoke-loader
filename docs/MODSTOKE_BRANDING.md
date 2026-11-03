# Modstoke Branding and Compatibility

Modstoke is the product brand, credited as **by OpenACAI**. OpenACAI remains the
nonprofit publisher. Existing copyright, license and contributor notices remain
in force.

| Product | Display name |
| --- | --- |
| Mod manager | Modstoke Manager |
| Loader | Modstoke Loader |
| Server administration toolkit | Modstoke Admin |
| Public modding API | Modstoke SDK |
| Website and mod catalog | Modstoke |

Modstoke Loader and Modstoke SDK support Sons of the Forest. They are independent
community projects, not affiliated with or endorsed by Endnight Games.
BepInEx, RedLoader and SonsSdk retain their names and upstream attribution.

## Website

The intended address is `modstoke.com`. Registration, HTTPS and replacement
services are not confirmed. Existing GitHub releases, update manifests, download
URLs and repository links remain in use. Branding does not authorize a redirect,
domain purchase or deployment.

## Compatibility

The source and API naming migration is a breaking change for compiled mods:

- Owned assemblies are `Modstoke.Loader`, `Modstoke.Bootstrap` and
  `Modstoke.Preloader`. APIs use `Modstoke.Loader`, `Modstoke.Sons` and
  `Modstoke.Jobs`, with concise type names. Rebuild mods against the new loader.
- BepInEx, RedLoader, SonsSdk, Harmony and Il2CppInterop compatibility identities
  are unchanged. The native host still deploys as `winhttp.dll`.
- Plugin GUID `com.openacai.sotf.endnightloader`, manifest ID
  `openacai.loader.sotf`, API/service keys, wire identifiers and compatibility
  modes remain unchanged.
- The game-root folder is now `Modstoke/` (`Modstoke/Runtime`, `Modstoke/Mods`,
  `Modstoke/UserData`). On first start the loader moves existing `OpenACAI/Mods`
  and `OpenACAI/UserData` contents into it without overwriting anything; entries
  that already exist are left in `OpenACAI/` and logged. `OpenACAI/Runtime` is
  left in place but no longer used.
- The BepInEx mirror `BepInEx/plugins/OpenACAILoader`, config filenames/keys,
  `openacai.mod.json`, environment variables and Unity object names stay put.
- Existing published archive names and URLs stay valid. Source builds use
  `build.ps1` and `loader.manifest.json` from the `Modstoke.Loader` repository folder.
- Mods, saves and user configuration are moved, not renamed or rewritten.

Use stable IDs rather than display names for identity checks. Candidate lookup
still accepts the old
`OpenACAI Endnight Loader Core` name for the unchanged Core plugin GUID.
The normal banner accepts the old manifest name and displays Modstoke Loader.

Runtime attestation includes the display name and package hashes. Regenerated
evidence therefore has different digests; the evidence format, token prefix and
service keys are unchanged. A digest is an observation, not an authentication
guarantee or a permanent product identifier.

## Other Projects

This repository supplies Loader/SDK branding and its bundled runtime patch.
Modstoke Manager, Modstoke Admin and the website/catalog require their own releases.
Manager integrations should keep using the existing manifest schema, URLs and
release filenames. Admin lives in a separate private `Modstoke.Admin` repository
and must be rebuilt with the same loader candidate as the public mods. Keep its
plugin IDs and settings paths; no private Admin code is included here.

## Artwork

`docs/assets/modstoke-mark.svg` uses the shared amber M and ember mark from
Modstoke Manager's `public/modstoke-mark.svg`. Keep its shape and colors consistent
across Loader, Manager, Admin, SDK and the catalog. OpenACAI remains the publisher;
the product mark does not replace publisher or third-party attribution.

Use `docs/assets/modstoke-loader-banner.svg` as the editable banner and the
matching PNG for services that require raster artwork. It follows Manager's
`docs/assets/modstoke-banner.svg` layout with the Loader product name and description.
Old banners, screenshots, patch filenames and quoted logs remain historical
evidence, not current branding.
Fresh in-game captures are required before updating the public screenshot gallery.
