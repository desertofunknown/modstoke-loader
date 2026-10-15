// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using Mono.Cecil;

namespace BepInEx.Preloader.Core;

/// <summary>Describes the framework and processor preference recorded in a managed assembly.</summary>
public class AssemblyBuildInfo
{
    /// <summary>The framework families recognized by the preloader.</summary>
    public enum FrameworkType
    {
        /// <summary>The framework family could not be identified.</summary>
        Unknown = 0,
        /// <summary>The assembly targets .NET Framework.</summary>
        NetFramework = 1,
        /// <summary>The assembly targets .NET Standard.</summary>
        NetStandard = 2,
        /// <summary>The assembly targets .NET Core or a later .NET runtime.</summary>
        NetCore = 3
    }

    /// <summary>Gets the detected framework version, or null before information is determined.</summary>
    public Version NetFrameworkVersion { get; private set; }

    /// <summary>Gets or sets whether either supported processor width can execute the assembly.</summary>
    public bool IsAnyCpu { get; set; }

    /// <summary>Gets or sets whether the assembly requires or prefers 64-bit execution.</summary>
    public bool Is64Bit { get; set; }

    /// <summary>Gets or sets the detected framework family.</summary>
    public FrameworkType AssemblyFrameworkType { get; set; }

    /// <summary>Reads framework metadata and the supported x86/x64 processor flags.</summary>
    /// <param name="assemblyDefinition">The assembly whose build information is required.</param>
    /// <returns>The detected framework and architecture information.</returns>
    /// <exception cref="Exception">The module architecture is neither I386 nor AMD64.</exception>
    public static AssemblyBuildInfo DetermineInfo(AssemblyDefinition assemblyDefinition)
    {
        var result = new AssemblyBuildInfo();
        var module = assemblyDefinition.MainModule;
        switch (module.Runtime)
        {
            case TargetRuntime.Net_1_0:
                result.NetFrameworkVersion = new Version(1, 0);
                result.AssemblyFrameworkType = FrameworkType.NetFramework;
                break;
            case TargetRuntime.Net_1_1:
                result.NetFrameworkVersion = new Version(1, 1);
                result.AssemblyFrameworkType = FrameworkType.NetFramework;
                break;
            case TargetRuntime.Net_2_0:
                // The compatibility contract reports CLR 2 assemblies as Framework 3.5.
                result.NetFrameworkVersion = new Version(3, 5);
                result.AssemblyFrameworkType = FrameworkType.NetFramework;
                break;
            default:
                result.ReadFrameworkMetadata(assemblyDefinition);
                break;
        }

        switch (module.Architecture)
        {
            case TargetArchitecture.AMD64:
                result.Is64Bit = true;
                break;
            case TargetArchitecture.I386:
                var requires32Bit = (module.Attributes & ModuleAttributes.Required32Bit) != 0;
                var prefers32Bit = (module.Attributes & ModuleAttributes.Preferred32Bit) != 0;
                result.Is64Bit = !requires32Bit;
                result.IsAnyCpu = !requires32Bit || prefers32Bit;
                break;
            default:
                throw new Exception("Unable to determine assembly architecture");
        }

        return result;
    }

    private void ReadFrameworkMetadata(AssemblyDefinition assembly)
    {
        NetFrameworkVersion = new Version(0, 0);
        AssemblyFrameworkType = FrameworkType.Unknown;

        var attribute = assembly.CustomAttributes.FirstOrDefault(candidate =>
            candidate.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
        if (attribute == null || attribute.ConstructorArguments.Count == 0
            || attribute.ConstructorArguments[0].Type.Name != "String")
            return;

        // FrameworkName rejects partial metadata accepted by the original contract.
        // Keep token order, whitespace sensitivity, and independent version recovery.
        var metadata = (string)attribute.ConstructorArguments[0].Value;
        foreach (var token in metadata.Split(','))
        {
            if (token.StartsWith(".NET"))
            {
                AssemblyFrameworkType = token switch
                {
                    ".NETFramework" => FrameworkType.NetFramework,
                    ".NETStandard" => FrameworkType.NetStandard,
                    ".NETCoreApp" => FrameworkType.NetCore,
                    _ => FrameworkType.Unknown
                };
            }
            else if (token.StartsWith("Version=v")
                && Version.TryParse(token.Substring("Version=v".Length), out var version))
            {
                NetFrameworkVersion = version;
            }
        }
    }

    /// <summary>Formats the framework version and processor requirement or AnyCPU preference.</summary>
    /// <returns>The compatibility display string for this build.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The framework family is not a defined enum value.</exception>
    public override string ToString()
    {
        var family = AssemblyFrameworkType switch
        {
            FrameworkType.Unknown => "Unknown",
            FrameworkType.NetFramework => "Framework",
            FrameworkType.NetStandard => "Standard",
            FrameworkType.NetCore => "Core",
            _ => throw new ArgumentOutOfRangeException()
        };
        var architecture = IsAnyCpu
            ? $"AnyCPU ({(Is64Bit ? "64" : "32")}-bit preferred)"
            : Is64Bit ? "x64" : "x86";
        return $".NET {family} {NetFrameworkVersion}, {architecture}";
    }
}
