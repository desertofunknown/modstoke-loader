# Managed Particle Jobs

`Modstoke.Jobs` exposes managed struct jobs for Unity particle systems. Reference
`Modstoke.Loader`, the matching `Il2CppInterop.Runtime`, and the
generated `UnityEngine.CoreModule` and `UnityEngine.ParticleSystemModule`.
The current native bindings target Windows x64 with Unity 2022.2.16.

```csharp
using Modstoke.Jobs;

public struct ApplyLift : IParticleParallelForJob
{
    public float VelocityChange;

    public void Execute(in ParticleJobData particles, int index)
    {
        var verticalVelocity = particles.Velocities.Y;
        if (!verticalVelocity.IsEmpty)
            verticalVelocity[index] += VelocityChange;
    }
}
```

Submit from the Unity main thread while holding a live `ParticleSystem`:

```csharp
var work = new ApplyLift { VelocityChange = 0.1f };
using var pending = ParticleJobs.ScheduleParallelFor(in work, system, 64);
pending.Complete();
```

Use `IParticleJob` with `Schedule` for a single whole-system callback, or
`IParticleParallelForBatchJob` with `ScheduleBatch` for contiguous ranges.
Parallel methods accept a positive batch size. Unity determines the particle
count after the preceding simulation dependencies; do not snapshot it yourself.
The SDK preserves the particle system's existing job dependency and publishes
the submitted handle back to that system.

## Data Access

`ParticleJobData` is a stack-only borrowed view. It exposes positions, velocities,
rotation axes, rotations, rotational speeds, sizes, start colors, normalized
ages (`AliveTimePercent`), inverse starting lifetimes, random seeds, custom data
1 and 2, and mesh indices. The channels describe the native simulation data,
not necessarily the final rendered color or size after every module.

Vector views expose component spans (`X`, `Y`, `Z`, and optionally `W`), a
value-reading indexer, and `Set(index, value)`. Scalar channels are spans. Missing
native channels produce empty spans, not synthetic buffers; vector
`IsAvailable` checks all components. Vector `Set` checks every component before
writing. No view constructs Unity `NativeArray` wrappers or owns an allocation.

An indexed job may write only its assigned particle. A batch job may write only
`[startIndex, startIndex + count)`. Do not mutate other particles or shared output
buffers concurrently. Retail Unity does not enforce this access discipline.

## Lifetime And Completion

The scheduler copies job payloads without copying changes back to the submitting
struct. Supported native-container payloads continue to share their backing
buffers. Keep those buffers and the particle system alive, and do not clear,
resize, destroy, or independently mutate them until the job completes.

Complete or dispose the returned `NativeJob`, or an owned downstream job, even
when `IsCompleted` returns true. `Complete` reports managed producer failures;
`Dispose` waits and releases ownership without rethrowing those producer errors.
If scheduling throws `NativeJobSchedulingException`, retain its `Job` and resolve
completion before releasing resources. Native internal completion alone cannot
release the managed ownership graph.

`ScheduleWithHandle`, `ScheduleParallelForWithHandle`, and `ScheduleBatchWithHandle`
instead accept and return Unity `JobHandle` values. They require the matching
preloader completion observers and regenerated wrappers. Prefer owner-returning
methods when that integration is not available.

Do not dispose borrowed channels, leak their pointers, call main-thread-only
Unity APIs, schedule jobs, or wait for jobs inside `Execute`. These SDK contracts
do not replace Unity's generated particle interfaces, enable Burst compilation,
or provide a synchronous `Run` method. The staged implementation has compile and
static-review evidence; native runtime acceptance remains separate.
