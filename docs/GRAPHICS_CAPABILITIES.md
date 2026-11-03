# Graphics capabilities

`Modstoke.Loader.GraphicsCapabilities` provides a read-only observation of Unity's
current graphics device. It does not enable graphics features, change settings or
own rendering resources.

## API and lifetime

- `Snapshot()` returns an immutable `GraphicsCapabilitiesSnapshot` from any thread.
  It never calls Unity and does not refresh the observation.
- `TryCapture(out GraphicsCapabilitiesSnapshot snapshot)` requires the initialized
  Unity lifecycle thread. Its output describes this attempt, not a fallback to the
  previous observation. It returns true only when the status is `Available`.
- Internal `Initialize()` binds the caller as the owner without reading Unity.
  Only the loader lifecycle may establish that this is the Unity thread. Calling
  it again on that thread preserves the cache; another thread cannot rebind it.

The snapshot contains managed strings, nullable scalars and a managed status enum.
It retains no Unity objects, native pointers, delegates, exception objects or
mutable collections. Cached access and publication share a lock. Native getters
run outside that lock, so readers retain the previous complete observation while
a capture is in progress. Thread binding lasts for the loader lifetime and cannot
be reset or transferred through the public API.

| Status | Meaning | Cache behavior |
| --- | --- | --- |
| `Unknown` | No completed observation. | Initial cache; no timestamp or capability values. |
| `Available` | Every selected getter returned for a non-Null device. | Atomically replaces the cache; capture returns true. |
| `Unavailable` | Unity returned `GraphicsDeviceType.Null`. | Replaces the cache with backend name/ID and timestamp only; capture returns false. |
| `Failed` | Thread ownership was missing/wrong, or capture threw. | Returned only to the caller; leaves the cache unchanged and returns false. |

`Failed.Detail` includes exception text for diagnostics. No failure is logged by
this API. A failed attempt discards all partial values. Consumers must inspect the
attempt's status rather than treating an older cached `Available` as fresh success.
Unobserved support flags and numbers are null, not false or zero. Device strings
and numeric values are copied as reported, without normalization or inference.
`ObservedUtc` is an ISO 8601 UTC completion time for `Available` and `Unavailable`;
it is not a device generation or proof that the device remains usable.

## Reported fields

| Snapshot field | `UnityEngine.SystemInfo` getter |
| --- | --- |
| `Backend`, `BackendId` | `graphicsDeviceType`, enum name and raw numeric value |
| `DeviceName` | `graphicsDeviceName` |
| `DeviceVendor` | `graphicsDeviceVendor` |
| `DeviceId` | `graphicsDeviceID` |
| `VendorId` | `graphicsDeviceVendorID` |
| `DeviceVersion` | `graphicsDeviceVersion`, unparsed |
| `ShaderLevel` | `graphicsShaderLevel`, raw Unity shader level |
| `SupportsComputeShaders` | `supportsComputeShaders` |
| `SupportsInstancing` | `supportsInstancing` |
| `SupportsGeometryShaders` | `supportsGeometryShaders` |
| `SupportsTessellationShaders` | `supportsTessellationShaders` |
| `SupportsAsyncCompute` | `supportsAsyncCompute` |
| `SupportsGraphicsFence` | `supportsGraphicsFence` |
| `SupportsRayTracing` | `supportsRayTracing` |
| `SupportsAsyncGpuReadback` | `supportsAsyncGPUReadback` |

These are support reports for the current device, not feature activation. Even an
`Available` snapshot with `SupportsRayTracing == true` says nothing about the
game's ray tracing settings or active render pipeline. Backend names and shader
levels do not establish DirectX feature level 12.1, DLSS, Multi Frame Generation,
ray reconstruction, or support for switching to Vulkan or Metal. Enum entries
for those backends do not prove this game can run them. No operating-system or
cross-platform compatibility claim is made.

## Reference boundary

The selected signatures were inspected in the current game compile reference,
`BepInEx/interop/UnityEngine.CoreModule.dll`, rather than assumed from a newer Unity
API. This generated assembly reports version `0.0.0.0`; that is not a Unity player
version. Its inspected module ID is `50d40db3-09d7-4b4b-a833-7c2bb0a484e1`, with
SHA256 `F8C5DB02E48D19CEB15025396D7E417CE91E8EDBBEF2D73B7F6260EF1F1E0258`.

All selected getters are public and static. Identity getters return managed
strings, integers or `GraphicsDeviceType`; selected support getters return
`System.Boolean`. `GraphicsDeviceType.Null` is present with value 4. The geometry,
tessellation and asynchronous GPU readback getters call restored helper methods
backed by internal-call delegates; the other selected getters use generated
IL2CPP invocation wrappers. Signature presence is not proof of successful native
execution. Missing bindings and other managed capture exceptions remain failures,
not unsupported-feature values. Native faults are not made recoverable by this API.

No `SystemInfo` getters named `supportsMeshShaders`, `supportsVariableRateShading`,
`supportsDLSS`, `supportsVulkan` or `supportsMetal` were present in this reference.
Those fields are intentionally not synthesized. The exposed support flags are a
bounded subset, not an exhaustive catalogue of every `SystemInfo` property.

## Integration boundary

The implementation is in
[`GraphicsCapabilities.cs`](../src/Modstoke.Loader/Runtime/Graphics/GraphicsCapabilities.cs).
The loader captures once through its application late-start event, after Unity
startup. That callback binds the Unity thread and records capture failures through
the existing startup-warning path. Loader status exposes the cached snapshot as
`graphics`; reading status never calls Unity. Capture does not run during static
initialization.

There is no periodic refresh, timer, render callback or retry.
Re-capture is explicit on the same owner thread. Existing snapshots can become
stale after device changes or shutdown; they are historical observations, not
resource leases. The implementation allocates only managed snapshot/diagnostic
data, not textures, buffers, command lists or other GPU resources. It adds no
hooks, settings, third-party dependency, device access or renderer mutation beyond
the selected Unity information getters.
