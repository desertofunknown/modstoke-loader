// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2022 BepInEx Team
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using Mono.Cecil;
using Version = SemanticVersioning.Version;

namespace BepInEx.Preloader.Core.Patching;

/// <summary>Supplies the identity used to register a patcher and create its log and configuration.</summary>
[AttributeUsage(AttributeTargets.Class)]
public class PatcherPluginInfoAttribute : Attribute
{
    /// <summary>Creates patcher metadata, accepting semantic and legacy CLR version strings.</summary>
    /// <param name="GUID">The stable identifier of the patcher.</param>
    /// <param name="Name">The display name of the patcher.</param>
    /// <param name="Version">The version text; an invalid value produces a null version.</param>
    public PatcherPluginInfoAttribute(string GUID, string Name, string Version)
    {
        this.GUID = GUID;
        this.Name = Name;
        this.Version = ParseVersion(Version);
    }

    /// <summary>Gets or sets the stable patcher identifier.</summary>
    public string GUID { get; protected set; }

    /// <summary>Gets or sets the patcher's display name.</summary>
    public string Name { get; protected set; }

    /// <summary>Gets or sets the parsed version, or null when the supplied text is invalid.</summary>
    public Version Version { get; protected set; }

    internal static PatcherPluginInfoAttribute FromType(Type type) =>
        type.GetCustomAttributes(typeof(PatcherPluginInfoAttribute), false)
            .Cast<PatcherPluginInfoAttribute>().FirstOrDefault();

    internal static PatcherPluginInfoAttribute FromCecilType(TypeDefinition td)
    {
        var metadata = MetadataHelper.GetCustomAttributes<PatcherPluginInfoAttribute>(td, false).FirstOrDefault();
        if (metadata == null)
            return null;

        var arguments = metadata.ConstructorArguments;
        return new PatcherPluginInfoAttribute((string)arguments[0].Value,
            (string)arguments[1].Value, (string)arguments[2].Value);
    }

    private static Version ParseVersion(string text)
    {
        if (Version.TryParse(text, out var semanticVersion))
            return semanticVersion;

        if (!System.Version.TryParse(text, out var clrVersion))
            return null;

        // CLR revisions are deliberately omitted from the semantic identity.
        return new Version(clrVersion.Major, clrVersion.Minor, Math.Max(0, clrVersion.Build));
    }
}

/// <summary>Identifies the assembly input for a patch method.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class TargetAssemblyAttribute : Attribute
{
    /// <summary>The sentinel selecting every available assembly.</summary>
    public const string AllAssemblies = "_all";

    /// <summary>Creates an assembly target.</summary>
    /// <param name="targetAssembly">An assembly filename, or <see cref="AllAssemblies"/>.</param>
    public TargetAssemblyAttribute(string targetAssembly) => TargetAssembly = targetAssembly;

    /// <summary>Gets the assembly filename or all-assemblies sentinel.</summary>
    public string TargetAssembly { get; }
}

/// <summary>Identifies the type input for a patch method.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class TargetTypeAttribute : Attribute
{
    /// <summary>Creates a type target within an assembly.</summary>
    /// <param name="targetAssembly">The filename of the containing assembly.</param>
    /// <param name="targetType">The full name of the type to patch.</param>
    public TargetTypeAttribute(string targetAssembly, string targetType)
    {
        TargetAssembly = targetAssembly;
        TargetType = targetType;
    }

    /// <summary>Gets the filename of the containing assembly.</summary>
    public string TargetAssembly { get; }

    /// <summary>Gets the full name of the target type.</summary>
    public string TargetType { get; }
}
