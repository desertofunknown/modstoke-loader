# Managed Transform Jobs

`Modstoke.Jobs.ITransformJob` lets a C# struct process a `TransformAccessArray`
through Unity's native transform scheduler. Reference `Modstoke.Loader`,
the matching `Il2CppInterop.Runtime`, and the generated `UnityEngine.CoreModule`.
This surface currently targets Windows x64 with Unity 2022.2.16.

```csharp
using Modstoke.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

public struct MoveTransforms : ITransformJob
{
    public Vector3 Offset;

    public void Execute(int index, TransformAccess transform)
    {
        transform.position += Offset;
    }
}
```

Submit from the Unity main thread with an existing, caller-owned array:

```csharp
var work = new MoveTransforms { Offset = new Vector3(0, 1, 0) };
using var pending = TransformJobs.Schedule(in work, in transforms);
pending.Complete();
```

`Execute` receives the original array index even when Unity sorts transforms by
hierarchy. The scheduler copies the job payload; modifications to that copy do
not update the submitting struct. Supported native container buffers remain
shared, so jobs must coordinate their accesses.

## Scheduling And Ownership

- `Schedule` preserves Unity's writable hierarchy scheduling.
- `ScheduleReadOnly` accepts a positive batch size and uses work stealing.
- `RunReadOnly` executes synchronously and reports callback failures before returning.
- `ScheduleWithHandle` and `ScheduleReadOnlyWithHandle` return Unity `JobHandle`
  values. These require the matching preloader's managed-handle completion observers.
  Prefer the owner-returning APIs unless a Unity handle is needed.

Pass a preceding `NativeJob` as the dependency when its managed ownership should
be completed with the new job. Native hierarchy dependencies alone do not release
unrelated managed owners. Complete or dispose every owner, even when `IsCompleted`
is true. `Complete` propagates recorded producer failures; `Dispose` releases
completed ownership without reporting producer failures.

Keep the transform array and every referenced native buffer alive and unchanged
until all jobs using them complete. Do not dispose them from the callback. A
`NativeJobSchedulingException` can retain a partially submitted job in its `Job`
property; preserve and resolve that owner instead of discarding it.

Read-only jobs must not write transforms. The retail player does not enforce that
promise. Transform access values are valid only during the callback; do not retain
them, schedule other jobs, wait on jobs, or call main-thread-only Unity APIs there.
Use supported value payloads and native containers, not ordinary managed object
references or automatic container-disposal attributes.

This is an explicit SDK contract. It leaves Unity's native
`IJobParallelForTransform` wrapper and existing game producers unchanged. It does
not enable Burst compilation or establish runtime acceptance of the staged loader.
