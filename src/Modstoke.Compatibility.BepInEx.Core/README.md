# Modstoke Core Compatibility

This project supplies the unsigned `BepInEx.Core, Version=6.0.0.0` assembly
for Modstoke Loader by OpenACAI. Existing plugins keep their assembly references,
configuration keys and paths. It requires the matching Modstoke preloader and
does not provide a standalone BepInEx runtime.

The chainloader lifecycle, plugin catalog, dependency ordering, activation
batches and metadata cache operations are implemented by Modstoke's preloader.
The compatibility types preserve virtual extension points, events, shared
collections and the original API signatures.

Configuration, logging, metadata, console, path and utility implementations
are retained from BepInEx under LGPL-2.1-only, including filesystem resolution
helpers and platform-specific console support. They are not wholly new
implementations. Existing source headers and embedded third-party attributions
remain intact. See the repository's `THIRD_PARTY_NOTICES.md` and
`LICENSES/LGPL-2.1.txt` for the upstream notices and license.
