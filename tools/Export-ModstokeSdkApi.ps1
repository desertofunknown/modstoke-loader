#requires -Version 7.2
<#
.SYNOPSIS
Exports public Modstoke.Sons declarations from a built loader DLL.
.DESCRIPTION
Reads ECMA-335 metadata without loading the input assembly or its dependencies.
Requires PowerShell 7.2 or later and its bundled framework reference assemblies.
The small metadata reader is compiled in memory; no game code is executed.
Output is deterministic for the same DLL bytes. Existing output is never replaced.
Signatures use metadata notation, not reconstructed C# source. Tokens identify
declarations only within the recorded sourceSHA256 and moduleVersionId.
.EXAMPLE
./tools/Export-ModstokeSdkApi.ps1 -CoreDllPath ./build/Modstoke.Loader.dll -OutputPath ./sdk-api.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$CoreDllPath,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourcePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CoreDllPath)
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if (-not [IO.File]::Exists($sourcePath)) { throw "Core DLL does not exist: $sourcePath" }
if (Test-Path -LiteralPath $destination) { throw "API inventory already exists: $destination" }

# Only the helper and PowerShell's framework references participate in compilation.
$readerSource = @'
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Modstoke.Tools.SdkApiInventory
{
    public sealed class Reader : ISignatureTypeProvider<string, object>
    {
        private const string NamespaceRoot = "Modstoke.Sons";
        private readonly MetadataReader metadata;
        private readonly string assemblyIdentity;
        private int specificationDepth;
        private int unavailableSignatures;

        private Reader(MetadataReader metadata, string assemblyIdentity)
        {
            this.metadata = metadata;
            this.assemblyIdentity = assemblyIdentity;
        }

        private static OrderedDictionary Record(params object[] pairs)
        {
            var result = new OrderedDictionary(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Token(EntityHandle handle) => "0x" + MetadataTokens.GetToken(handle).ToString("X8", CultureInfo.InvariantCulture);
        private string Text(StringHandle handle) => metadata.GetString(handle);
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        private static string PublicKeyToken(byte[] key, bool isFullKey)
        {
            if (key.Length == 0) return "null";
            if (!isFullKey) return Hex(key).ToLowerInvariant();
            byte[] hash = SHA1.HashData(key);
            byte[] token = new byte[8];
            for (int i = 0; i < token.Length; i++) token[i] = hash[hash.Length - 1 - i];
            return Hex(token).ToLowerInvariant();
        }

        private static string Identity(string name, Version version, string culture, string token, AssemblyFlags flags)
        {
            return name + ", Version=" + version + ", Culture=" + (culture.Length == 0 ? "neutral" : culture)
                + ", PublicKeyToken=" + token
                + ((flags & AssemblyFlags.Retargetable) != 0 ? ", Retargetable=Yes" : "")
                + ((flags & AssemblyFlags.WindowsRuntime) != 0 ? ", ContentType=WindowsRuntime" : "");
        }

        public static OrderedDictionary Read(string path)
        {
            // Hash and inspect the same immutable copy, even if the source is rebuilt later.
            byte[] image = File.ReadAllBytes(path);
            string sourceHash = Hex(SHA256.HashData(image));
            using (var stream = new MemoryStream(image, false))
            using (var pe = new PEReader(stream))
            {
                if (!pe.HasMetadata) throw new InvalidDataException("The input is not a managed PE image.");
                MetadataReader metadata = pe.GetMetadataReader(MetadataReaderOptions.None);
                if (!metadata.IsAssembly) throw new InvalidDataException("The input is not an assembly manifest.");
                AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
                string name = metadata.GetString(assembly.Name);
                if (!String.Equals(name, "Modstoke.Loader", StringComparison.Ordinal))
                    throw new InvalidDataException("Expected Modstoke.Loader, received " + name + ".");
                string culture = metadata.GetString(assembly.Culture);
                string keyToken = PublicKeyToken(metadata.GetBlobBytes(assembly.PublicKey), true);
                string identity = Identity(name, assembly.Version, culture, keyToken, assembly.Flags);
                var reader = new Reader(metadata, identity);
                var types = new List<OrderedDictionary>();
                int methods = 0, properties = 0, fields = 0;
                foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
                {
                    if (!reader.IsPublicType(handle)) continue;
                    string ns = reader.TypeNamespace(handle);
                    if (ns != NamespaceRoot && !ns.StartsWith(NamespaceRoot + ".", StringComparison.Ordinal)) continue;
                    OrderedDictionary type = reader.ReadType(handle);
                    methods += ((List<OrderedDictionary>)type["methods"]).Count;
                    properties += ((List<OrderedDictionary>)type["properties"]).Count;
                    fields += ((List<OrderedDictionary>)type["fields"]).Count;
                    types.Add(type);
                }
                // Metadata row order is independent of locale and stable for identical source bytes.
                ModuleDefinition module = metadata.GetModuleDefinition();
                return Record(
                    "schema", 1,
                    "source", "ECMA-335 metadata; input assembly is not loaded",
                    "sourceSHA256", sourceHash,
                    "assembly", Record("name", name, "version", assembly.Version.ToString(), "culture", culture,
                        "publicKeyToken", keyToken, "fullName", identity, "flags", assembly.Flags.ToString()),
                    "moduleName", metadata.GetString(module.Name),
                    "moduleVersionId", metadata.GetGuid(module.Mvid).ToString("D"),
                    "namespaceRoot", NamespaceRoot,
                    "declaredOnly", true,
                    "truncated", false,
                    "signatureFormat", "ECMA-335 types: assembly-qualified names, !n type parameters, !!n method parameters; not C# source",
                    "limitations", new[] {
                        "Only externally public type definitions and their declared public methods, properties and fields are listed.",
                        "Inherited members, forwarded types, event declarations and custom attributes are not expanded.",
                        "Nullable, tuple-name, dynamic and other custom-attribute C# annotations are not reconstructed.",
                        "Property accessors and constructors also appear as public method declarations; private accessors are omitted.",
                        "Constants are metadata type codes and raw blobs, never evaluated runtime field values.",
                        "Declaration identity is sourceSHA256 plus metadataToken; tokens are not native addresses or IL2CPP original tokens.",
                        "Metadata describes declarations only, not runtime readiness, native implementation or call safety."
                    },
                    "typeCount", types.Count, "methodCount", methods, "propertyCount", properties, "fieldCount", fields,
                    "unavailableSignatureCount", reader.unavailableSignatures, "types", types);
            }
        }

        private bool IsPublicType(TypeDefinitionHandle handle, int depth = 0)
        {
            CheckDepth(depth);
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
            TypeDefinitionHandle parent = type.GetDeclaringType();
            return parent.IsNil ? visibility == TypeAttributes.Public
                : visibility == TypeAttributes.NestedPublic && IsPublicType(parent, depth + 1);
        }

        private string TypeNamespace(TypeDefinitionHandle handle, int depth = 0)
        {
            CheckDepth(depth);
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            TypeDefinitionHandle parent = type.GetDeclaringType();
            return parent.IsNil ? Text(type.Namespace) : TypeNamespace(parent, depth + 1);
        }

        private string TypeName(TypeDefinitionHandle handle, int depth = 0)
        {
            CheckDepth(depth);
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            TypeDefinitionHandle parent = type.GetDeclaringType();
            if (!parent.IsNil) return TypeName(parent, depth + 1) + "+" + Text(type.Name);
            string ns = Text(type.Namespace);
            return (ns.Length == 0 ? "" : ns + ".") + Text(type.Name);
        }

        private static void CheckDepth(int depth)
        {
            if (depth >= 64) throw new NotSupportedException("Type nesting exceeds the supported depth of 64.");
        }

        private bool IsPublicMethod(MethodDefinitionHandle handle) => !handle.IsNil
            && (metadata.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;

        private OrderedDictionary ReadType(TypeDefinitionHandle handle)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            string fullName = TypeName(handle);
            string qualifiedName = "[" + assemblyIdentity + "]" + fullName;
            var methods = new List<OrderedDictionary>();
            var properties = new List<OrderedDictionary>();
            var fields = new List<OrderedDictionary>();
            foreach (MethodDefinitionHandle method in type.GetMethods())
                if (IsPublicMethod(method)) methods.Add(ReadMethod(method, qualifiedName));
            foreach (PropertyDefinitionHandle property in type.GetProperties())
            {
                PropertyDefinition definition = metadata.GetPropertyDefinition(property);
                PropertyAccessors accessors = definition.GetAccessors();
                var visible = new List<OrderedDictionary>();
                AddAccessor(visible, accessors.Getter, "get");
                AddAccessor(visible, accessors.Setter, "set");
                foreach (MethodDefinitionHandle other in accessors.Others) AddAccessor(visible, other, "other");
                if (visible.Count != 0) properties.Add(ReadProperty(property, qualifiedName, visible));
            }
            foreach (FieldDefinitionHandle field in type.GetFields())
            {
                FieldDefinition definition = metadata.GetFieldDefinition(field);
                if ((definition.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public)
                    fields.Add(ReadField(field, qualifiedName));
            }
            var interfaces = new List<OrderedDictionary>();
            foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
                interfaces.Add(ReadTypeSignature(metadata.GetInterfaceImplementation(implementation).Interface));
            return Record("name", Text(type.Name), "namespace", TypeNamespace(handle), "fullName", fullName,
                "metadataToken", Token(handle), "attributes", type.Attributes.ToString(),
                "declaration", "public " + ((type.Attributes & TypeAttributes.Interface) != 0 ? "interface " : "type ") + qualifiedName,
                "baseType", type.BaseType.IsNil ? null : ReadTypeSignature(type.BaseType),
                "interfaces", interfaces, "genericParameters", ReadGenericParameters(type.GetGenericParameters()),
                "methods", methods, "properties", properties, "fields", fields);
        }

        private void AddAccessor(List<OrderedDictionary> accessors, MethodDefinitionHandle handle, string kind)
        {
            if (IsPublicMethod(handle)) accessors.Add(Record("kind", kind, "metadataToken", Token(handle)));
        }

        private OrderedDictionary Member(EntityHandle handle, string name, object attributes, BlobHandle signature)
        {
            return Record("name", name, "metadataToken", Token(handle), "attributes", attributes.ToString(),
                "signatureBlobHex", Hex(metadata.GetBlobBytes(signature)), "signatureStatus", "decoded",
                "unavailableReason", null, "signature", null, "declaration", null);
        }

        private static bool IsSignatureFailure(Exception error) => error is BadImageFormatException
            || error is NotSupportedException || error is ArgumentException || error is InvalidOperationException;

        private void Unavailable(OrderedDictionary record, Exception error)
        {
            record["signatureStatus"] = "unavailable";
            // Exception messages can vary by locale or embed paths; keep the exported reason stable.
            record["unavailableReason"] = error is NotSupportedException ? "Unsupported signature or type nesting."
                : "Invalid or unsupported metadata signature (" + error.GetType().Name + ").";
            record["signature"] = null;
            record["declaration"] = null;
            unavailableSignatures++;
        }

        private MethodSignature<string> DecodeMethod(BlobHandle handle)
        {
            BlobReader blob = metadata.GetBlobReader(handle);
            var decoder = new SignatureDecoder<string, object>(this, metadata, null);
            MethodSignature<string> signature = decoder.DecodeMethodSignature(ref blob);
            if (blob.RemainingBytes != 0) throw new NotSupportedException("Trailing signature bytes.");
            return signature;
        }

        private OrderedDictionary ReadMethod(MethodDefinitionHandle handle, string declaringType)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            string name = Text(method.Name);
            OrderedDictionary result = Member(handle, name, method.Attributes, method.Signature);
            result.Add("implementationAttributes", method.ImplAttributes.ToString());
            result.Add("genericParameters", ReadGenericParameters(method.GetGenericParameters()));
            var parameters = new List<OrderedDictionary>();
            foreach (ParameterHandle parameterHandle in method.GetParameters())
            {
                Parameter parameter = metadata.GetParameter(parameterHandle);
                parameters.Add(Record("sequence", parameter.SequenceNumber, "name", Text(parameter.Name),
                    "metadataToken", Token(parameterHandle), "attributes", parameter.Attributes.ToString(),
                    "defaultConstant", ReadConstant(parameter.GetDefaultValue())));
            }
            parameters.Sort((left, right) => ((int)left["sequence"]).CompareTo((int)right["sequence"]));
            result.Add("parameterMetadata", parameters);
            try
            {
                MethodSignature<string> signature = DecodeMethod(method.Signature);
                result["signature"] = SignatureRecord(signature);
                result["declaration"] = "public method " + FormatMethod(signature, declaringType + "::" + name);
            }
            catch (Exception error) when (IsSignatureFailure(error)) { Unavailable(result, error); }
            return result;
        }

        private OrderedDictionary ReadProperty(PropertyDefinitionHandle handle, string declaringType, List<OrderedDictionary> accessors)
        {
            PropertyDefinition property = metadata.GetPropertyDefinition(handle);
            OrderedDictionary result = Member(handle, Text(property.Name), property.Attributes, property.Signature);
            result.Add("publicAccessors", accessors);
            result.Add("defaultConstant", ReadConstant(property.GetDefaultValue()));
            try
            {
                MethodSignature<string> signature = DecodeMethod(property.Signature);
                result["signature"] = SignatureRecord(signature);
                var names = new List<string>();
                foreach (OrderedDictionary accessor in accessors) names.Add((string)accessor["kind"] + ";");
                result["declaration"] = "public property " + FormatMethod(signature, declaringType + "::" + Text(property.Name))
                    + " { " + String.Join(" ", names) + " }";
            }
            catch (Exception error) when (IsSignatureFailure(error)) { Unavailable(result, error); }
            return result;
        }

        private OrderedDictionary ReadField(FieldDefinitionHandle handle, string declaringType)
        {
            FieldDefinition field = metadata.GetFieldDefinition(handle);
            OrderedDictionary result = Member(handle, Text(field.Name), field.Attributes, field.Signature);
            result.Add("defaultConstant", ReadConstant(field.GetDefaultValue()));
            try
            {
                BlobReader blob = metadata.GetBlobReader(field.Signature);
                var decoder = new SignatureDecoder<string, object>(this, metadata, null);
                string signature = decoder.DecodeFieldSignature(ref blob);
                if (blob.RemainingBytes != 0) throw new NotSupportedException("Trailing signature bytes.");
                result["signature"] = signature;
                result["declaration"] = "public " + ((field.Attributes & FieldAttributes.Static) != 0 ? "static " : "instance ")
                    + ((field.Attributes & FieldAttributes.Literal) != 0 ? "literal " : "")
                    + ((field.Attributes & FieldAttributes.InitOnly) != 0 ? "initonly " : "")
                    + "field " + signature + " " + declaringType + "::" + Text(field.Name);
            }
            catch (Exception error) when (IsSignatureFailure(error)) { Unavailable(result, error); }
            return result;
        }

        private OrderedDictionary ReadConstant(ConstantHandle handle)
        {
            if (handle.IsNil) return null;
            Constant constant = metadata.GetConstant(handle);
            return Record("typeCode", constant.TypeCode.ToString(), "blobHex", Hex(metadata.GetBlobBytes(constant.Value)));
        }

        private List<OrderedDictionary> ReadGenericParameters(GenericParameterHandleCollection handles)
        {
            var parameters = new List<OrderedDictionary>();
            foreach (GenericParameterHandle handle in handles)
            {
                GenericParameter parameter = metadata.GetGenericParameter(handle);
                var constraints = new List<OrderedDictionary>();
                foreach (GenericParameterConstraintHandle constraint in parameter.GetConstraints())
                    constraints.Add(ReadTypeSignature(metadata.GetGenericParameterConstraint(constraint).Type));
                parameters.Add(Record("index", parameter.Index, "name", Text(parameter.Name), "metadataToken", Token(handle),
                    "attributes", parameter.Attributes.ToString(), "constraints", constraints));
            }
            parameters.Sort((left, right) => ((int)left["index"]).CompareTo((int)right["index"]));
            return parameters;
        }

        private OrderedDictionary ReadTypeSignature(EntityHandle handle)
        {
            OrderedDictionary result = Record("metadataToken", Token(handle), "signatureStatus", "decoded",
                "unavailableReason", null, "signature", null, "declaration", null);
            try { result["signature"] = ResolveType(handle); }
            catch (Exception error) when (IsSignatureFailure(error)) { Unavailable(result, error); }
            return result;
        }

        private string ResolveType(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition: return "[" + assemblyIdentity + "]" + TypeName((TypeDefinitionHandle)handle);
                case HandleKind.TypeReference: return ReferenceName((TypeReferenceHandle)handle);
                case HandleKind.TypeSpecification: return GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)handle, 0);
                default: throw new NotSupportedException("Unsupported type handle.");
            }
        }

        private string ReferenceName(TypeReferenceHandle handle, int depth = 0)
        {
            CheckDepth(depth);
            TypeReference type = metadata.GetTypeReference(handle);
            EntityHandle scope = type.ResolutionScope;
            if (scope.IsNil) throw new NotSupportedException("Type-reference resolution scope is unavailable.");
            if (scope.Kind == HandleKind.TypeReference)
                return ReferenceName((TypeReferenceHandle)scope, depth + 1) + "+" + Text(type.Name);
            string owner;
            switch (scope.Kind)
            {
                case HandleKind.AssemblyReference:
                    AssemblyReference reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
                    owner = Identity(Text(reference.Name), reference.Version, Text(reference.Culture),
                        PublicKeyToken(metadata.GetBlobBytes(reference.PublicKeyOrToken), (reference.Flags & AssemblyFlags.PublicKey) != 0), reference.Flags);
                    break;
                case HandleKind.ModuleDefinition:
                    owner = assemblyIdentity;
                    break;
                case HandleKind.ModuleReference:
                    owner = assemblyIdentity + "; module=" + Text(metadata.GetModuleReference((ModuleReferenceHandle)scope).Name);
                    break;
                default: throw new NotSupportedException("Unresolved type-reference scope.");
            }
            string ns = Text(type.Namespace);
            return "[" + owner + "]" + (ns.Length == 0 ? "" : ns + ".") + Text(type.Name);
        }

        private static OrderedDictionary SignatureRecord(MethodSignature<string> signature)
        {
            return Record("headerByte", "0x" + signature.Header.RawValue.ToString("X2", CultureInfo.InvariantCulture),
                "callingConvention", signature.Header.CallingConvention.ToString(), "isInstance", signature.Header.IsInstance,
                "explicitThis", signature.Header.HasExplicitThis, "genericParameterCount", signature.GenericParameterCount,
                "returnType", signature.ReturnType, "requiredParameterCount", signature.RequiredParameterCount,
                "parameterTypes", new List<string>(signature.ParameterTypes));
        }

        private static string FormatMethod(MethodSignature<string> signature, string name)
        {
            var parameters = new List<string>();
            for (int i = 0; i < signature.ParameterTypes.Length; i++)
            {
                if (i == signature.RequiredParameterCount) parameters.Add("...");
                parameters.Add(signature.ParameterTypes[i]);
            }
            return (signature.Header.IsInstance ? "instance " : "static ")
                + (signature.Header.HasExplicitThis ? "explicitthis " : "")
                + signature.Header.CallingConvention + " " + signature.ReturnType + " " + name
                + (signature.GenericParameterCount == 0 ? "" : "``" + Number(signature.GenericParameterCount))
                + "(" + String.Join(", ", parameters) + ")";
        }

        public string GetArrayType(string elementType, ArrayShape shape)
        {
            var sizes = new List<string>();
            var bounds = new List<string>();
            foreach (int size in shape.Sizes) sizes.Add(Number(size));
            foreach (int bound in shape.LowerBounds) bounds.Add(Number(bound));
            return elementType + "[rank=" + Number(shape.Rank) + ";sizes=" + String.Join(",", sizes)
                + ";lowerBounds=" + String.Join(",", bounds) + "]";
        }

        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr(" + FormatMethod(signature, "*") + ")";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + String.Join(", ", typeArguments) + ">";
        public string GetGenericMethodParameter(object context, int index) => "!!" + Number(index);
        public string GetGenericTypeParameter(object context, int index) => "!" + Number(index);
        public string GetModifiedType(string modifier, string type, bool required) => type + (required ? " modreq(" : " modopt(") + modifier + ")";
        public string GetPinnedType(string elementType) => elementType + " pinned";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "primitive(" + typeCode + ")";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind) => Kind(kind) + ResolveType(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind) => Kind(kind) + ReferenceName(handle);

        private static string Kind(byte kind)
        {
            if (kind == 0) return "";
            if (kind == 0x11) return "valuetype ";
            if (kind == 0x12) return "class ";
            throw new NotSupportedException("Unknown signature type kind.");
        }

        public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte kind)
        {
            CheckDepth(specificationDepth);
            specificationDepth++;
            try
            {
                BlobReader blob = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
                var decoder = new SignatureDecoder<string, object>(this, metadata, null);
                string type = decoder.DecodeType(ref blob);
                if (blob.RemainingBytes != 0) throw new NotSupportedException("Trailing signature bytes.");
                return type;
            }
            finally { specificationDepth--; }
        }
    }
}
'@

if (-not ('Modstoke.Tools.SdkApiInventory.Reader' -as [type])) {
    $referenceDirectory = Join-Path $PSHOME 'ref'
    foreach ($name in @('System.Reflection.Metadata.dll', 'System.Collections.Immutable.dll')) {
        if (-not [IO.File]::Exists((Join-Path $referenceDirectory $name))) {
            throw "PowerShell framework reference is unavailable: $name. Use a complete PowerShell 7.2 or later installation."
        }
    }
    $references = @(Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' -File | Select-Object -ExpandProperty FullName)
    Add-Type -TypeDefinition $readerSource -ReferencedAssemblies $references
}

$document = [Modstoke.Tools.SdkApiInventory.Reader]::Read($sourcePath)
$json = ($document | ConvertTo-Json -Depth 32 -WarningAction Stop).Replace("`r`n", "`n")
$directory = [IO.Path]::GetDirectoryName($destination)
[void][IO.Directory]::CreateDirectory($directory)
$temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($destination) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$ownsTemporary = $false
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $ownsTemporary = $true
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
    # The two-argument move cannot replace an output created after the initial check.
    [IO.File]::Move($temporary, $destination)
    $ownsTemporary = $false
}
finally {
    if ($ownsTemporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
Write-Output ("Indexed {0} public types, {1} methods, {2} properties and {3} fields ({4} unavailable signatures): {5}" -f
    $document.typeCount, $document.methodCount, $document.propertyCount, $document.fieldCount,
    $document.unavailableSignatureCount, $destination)
