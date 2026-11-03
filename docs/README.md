# Documentation

Start with [Source and Build](NEXUS_SOURCE_AND_BUILD.md) to build the loader, or
the [SDK guide](sdk.md) to write a mod. See
[Project Structure](PROJECT_STRUCTURE.md) for repository boundaries and the
[Core guide](../src/Modstoke.Loader/README.md) for source navigation.

## Mod development

- [Development workflow](SDK_DEVELOPMENT_WORKFLOW.md): build references and SDK usage.
- [Game routines](SDK_GAME_ROUTINES.md): finding game calls and their contracts.
- [Runtime actions](SOTF_RUNTIME_ACTIONS.md): supported game actions and limits.
- [Bolt networking](BOLT_NETWORKING.md), [event policy](BOLT_EVENT_POLICY.md) and
  [ownership migration](BOLT_OWNERSHIP_MIGRATION.md).
- [Combat](COMBAT_CONTRACT.md), [ballistic reads](BALLISTIC_READS.md) and
  [collision reads](SERVER_COLLISION_READS.md).
- [Companion jobs](COMPANION_JOBS.md), [NPC parties](NPC_PARTIES.md) and
  [named states](NAMED_STATE_ACCESS.md).
- [Particle jobs](UNITY_PARTICLE_JOBS.md) and [transform jobs](UNITY_TRANSFORM_JOBS.md).
- [Preview stage and screen host](PREVIEW_STAGE_AND_SCREEN_HOST.md): character previews and full-screen editors.

## Loader development

- [Contributing](../CONTRIBUTING.md): code conventions and build expectations.
- [Runtime compatibility](SOTF_RUNTIME_COMPATIBILITY.md): game and runtime constraints.
- [.NET 11 branch](NET11_PREVIEW_EXPERIMENT.md): version pins and preview limits.
- [Mod dependencies](mod-dependencies.md): startup order and required mod versions.
- [Bootstrap notes](DOORSTOP_RUNTIME_NOTES.md): native-to-managed startup.
- [Runtime architecture](runtime.md) and
  [UI architecture](ui.md).

## Distribution

- [Project ownership](PROJECT_ORGANIZATION.md) and [public mod platform](mod-platform.md).
- [Branding and compatibility](MODSTOKE_BRANDING.md).
- [Dependency sources](../UPSTREAMS.md), [third-party notices](../THIRD_PARTY_NOTICES.md)
  and [licenses](../LICENSES).

Architecture and roadmap documents include proposed work. Use the relevant API
contract and build notes to distinguish implemented support from plans.
