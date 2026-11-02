using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length is not (2 or 3))
{
    Console.Error.WriteLine("Usage: 7zmetadataSample <winmd> <namespace-map.json>");
    Console.Error.WriteLine("       7zmetadataSample <baseline.winmd> <updated.winmd> <namespace-map.json>");
    return 2;
}
var mapping = JsonSerializer.Deserialize<NamespaceMap>(File.ReadAllText(args[^1]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
using var updated = new ContractReader(args[^2], mapping, normalizeLegacyNames: false);
updated.ValidateNamespaces(mapping);
if (args.Length == 3)
{
    using var baseline = new ContractReader(args[0], mapping, normalizeLegacyNames: true);
    var before = baseline.Contracts().ToHashSet(StringComparer.Ordinal);
    var after = updated.Contracts().ToHashSet(StringComparer.Ordinal);
    string[] removed = before.Except(after).Order(StringComparer.Ordinal).ToArray();
    string[] added = after.Except(before).Order(StringComparer.Ordinal).ToArray();
    if (removed.Length != 0 || added.Length != 0)
    {
        foreach (string change in removed.Take(12)) Console.Error.WriteLine("- " + change);
        foreach (string change in added.Take(12)) Console.Error.WriteLine("+ " + change);
        Console.Error.WriteLine($"Unexpected ABI changes: {removed.Length} removed, {added.Length} added contracts.");
        return 1;
    }
    Console.WriteLine($"Verified {after.Count} type/member contracts; GUIDs, inheritance, layouts, method order, signatures, and constants are unchanged.");
}
foreach (var group in updated.TypeNames.Where(name => name.StartsWith("SevenZip.Interop.", StringComparison.Ordinal))
             .GroupBy(name => name[..name.LastIndexOf('.')]).OrderBy(group => group.Key))
{
    Console.WriteLine($"{group.Key}: {group.Count()} types");
    if (args.Length == 2)
        foreach (string type in group.Order(StringComparer.Ordinal)) Console.WriteLine("  " + type);
}
return 0;

internal sealed record NamespaceMap(Dictionary<string, string> Headers, Dictionary<string, string> Types);

internal sealed class ContractReader : ISignatureTypeProvider<string, object?>, IDisposable
{
    private readonly FileStream stream;
    private readonly PEReader pe;
    private readonly MetadataReader reader;
    private readonly NamespaceMap mapping;
    private readonly bool normalizeLegacyNames;
    private readonly Dictionary<string, string> legacyNamespaces;

    internal ContractReader(string path, NamespaceMap mapping, bool normalizeLegacyNames)
    {
        stream = File.OpenRead(path);
        pe = new(stream);
        reader = pe.GetMetadataReader(MetadataReaderOptions.None);
        this.mapping = mapping;
        this.normalizeLegacyNames = normalizeLegacyNames;
        legacyNamespaces = mapping.Headers.ToDictionary(
            pair => "SevenZip.Interop.Headers." + Regex.Replace(pair.Key, "[^A-Za-z0-9_]", "_"),
            pair => pair.Value, StringComparer.Ordinal);
    }

    internal IEnumerable<string> TypeNames => reader.TypeDefinitions.Select(handle => GetTypeFromDefinition(reader, handle, 0));

    internal void ValidateNamespaces(NamespaceMap map)
    {
        var allowed = map.Headers.Values.Concat(map.Types.Values).ToHashSet(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            string ns = reader.GetString(type.Namespace);
            string name = reader.GetString(type.Name);
            if (ns.StartsWith("SevenZip.", StringComparison.Ordinal) && !allowed.Contains(ns))
                throw new InvalidDataException($"Unexpected namespace: {ns}.{name}");
            if (map.Types.TryGetValue(name, out string? expected) && ns != expected)
                throw new InvalidDataException($"Expected {expected}.{name}; got {ns}.{name}");
        }
        foreach (var handle in reader.TypeReferences)
        {
            var type = reader.GetTypeReference(handle);
            if (reader.GetString(type.Namespace).StartsWith("SevenZip.Interop.Headers.", StringComparison.Ordinal))
                throw new InvalidDataException("A type reference still uses a header-derived namespace.");
        }
        foreach (string required in map.Types.Select(pair => pair.Value + "." + pair.Key).Concat(new[]
        {
            "SevenZip.Interop.Compression.ICompressGetInStreamProcessedSize2",
            "SevenZip.Interop.Progress.IProgress"
        }))
            if (!TypeNames.Contains(required, StringComparer.Ordinal))
                throw new InvalidDataException($"Required type missing: {required}");
    }

    internal IEnumerable<string> Contracts()
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            string name = GetTypeFromDefinition(reader, handle, 0);
            var layout = type.GetLayout();
            string bases = string.Join(",", type.GetInterfaceImplementations()
                .Select(value => EntityName(reader.GetInterfaceImplementation(value).Interface)));
            yield return $"type {name} flags={(int)type.Attributes} base={EntityName(type.BaseType)} interfaces={bases} layout={layout.PackingSize},{layout.Size}";
            foreach (var attributeHandle in type.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                string constructor = attribute.Constructor.Kind == HandleKind.MemberReference
                    ? EntityName(reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent)
                    : EntityName(reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
                if (constructor.EndsWith(".GuidAttribute", StringComparison.Ordinal))
                    yield return $"guid {name} {Convert.ToHexString(reader.GetBlobBytes(attribute.Value))}";
            }
            int index = 0;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                string parameters = string.Join(";", method.GetParameters().Select(parameterHandle =>
                {
                    var parameter = reader.GetParameter(parameterHandle);
                    var marshal = parameter.GetMarshallingDescriptor();
                    return $"{parameter.SequenceNumber}:{reader.GetString(parameter.Name)}:{(int)parameter.Attributes}:" +
                        (marshal.IsNil ? "" : Convert.ToHexString(reader.GetBlobBytes(marshal)));
                }));
                yield return $"method {name}[{index++}] {reader.GetString(method.Name)} flags={(int)method.Attributes},{(int)method.ImplAttributes} " +
                    $"{Signature(method.DecodeSignature(this, null))} params={parameters}";
            }
            index = 0;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                string constant = "";
                if (!field.GetDefaultValue().IsNil)
                {
                    var value = reader.GetConstant(field.GetDefaultValue());
                    constant = $"{value.TypeCode}:{Convert.ToHexString(reader.GetBlobBytes(value.Value))}";
                }
                yield return $"field {name}[{index++}] {reader.GetString(field.Name)} {field.DecodeSignature(this, null)} " +
                    $"flags={(int)field.Attributes} offset={field.GetOffset()} constant={constant}";
            }
        }
    }

    private string Name(string ns, string name)
    {
        if (normalizeLegacyNames && legacyNamespaces.TryGetValue(ns, out string? replacement))
            ns = mapping.Types.TryGetValue(name, out string? specific) ? specific : replacement;
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private string EntityName(EntityHandle handle) => handle.IsNil ? "" : handle.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
        _ => throw new NotSupportedException($"Unexpected metadata handle {handle.Kind}.")
    };

    private static string Signature(MethodSignature<string> signature) =>
        $"{signature.Header.RawValue}:{signature.GenericParameterCount}:{signature.RequiredParameterCount}:" +
        $"{signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";

    public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = metadata.GetTypeDefinition(handle);
        return type.GetDeclaringType().IsNil ? Name(metadata.GetString(type.Namespace), metadata.GetString(type.Name))
            : GetTypeFromDefinition(metadata, type.GetDeclaringType(), rawTypeKind) + "+" + metadata.GetString(type.Name);
    }
    public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = metadata.GetTypeReference(handle);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? GetTypeFromReference(metadata, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "+" + metadata.GetString(type.Name)
            : Name(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
    }
    public string GetTypeFromSpecification(MetadataReader metadata, object? context, TypeSpecificationHandle handle, byte rawTypeKind)
        => metadata.GetTypeSpecification(handle).DecodeSignature(this, context);
    public string GetArrayType(string elementType, ArrayShape shape)
        => $"{elementType}[{shape.Rank};{string.Join(",", shape.Sizes)};{string.Join(",", shape.LowerBounds)}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*" + Signature(signature);
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? context, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => $"{(isRequired ? "modreq" : "modopt")}({modifier}){unmodifiedType}";
    public string GetPinnedType(string elementType) => "pinned " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public void Dispose() { pe.Dispose(); stream.Dispose(); }
}
