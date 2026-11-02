using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class GenerateSevenZipInputs : Task
{
    private const string GeneratedComment = "// Generated from external/7zip/CPP/7zip/Guid.txt. Do not edit by hand.";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    [Required] public string CppRoot { get; set; }
    [Required] public string NamespaceMapPath { get; set; }
    [Required] public string PartitionsRoot { get; set; }
    [Required] public string ScraperHeaderPath { get; set; }
    [Required] public string NamespaceResponsePath { get; set; }
    [Output] public ITaskItem[] Partitions { get; set; }

    public override bool Execute()
    {
        try
        {
            Generate();
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception);
            return false;
        }
    }

    private void Generate()
    {
        string cppRoot = Path.GetFullPath(CppRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string headersRoot = Path.Combine(cppRoot, "7zip");
        string guidPath = Path.Combine(headersRoot, "Guid.txt");
        if (!File.Exists(guidPath))
        {
            throw new FileNotFoundException("7-Zip submodule is missing. Run 'git submodule update --init --recursive' first.", guidPath);
        }

        Dictionary<string, Dictionary<string, string>> namespaceMap;
        var serializer = new DataContractJsonSerializer(
            typeof(Dictionary<string, Dictionary<string, string>>),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using (var stream = File.OpenRead(NamespaceMapPath))
        {
            namespaceMap = (Dictionary<string, Dictionary<string, string>>)serializer.ReadObject(stream);
        }

        string catalog = File.ReadAllText(guidPath);
        int handlersStart = catalog.IndexOf("Handler GUIDs:", StringComparison.Ordinal);
        if (handlersStart >= 0)
        {
            catalog = catalog.Substring(0, handlersStart);
        }

        var publicHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var internalHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(catalog, @"(?im)^\s*(?://\s*)?[0-9A-F]{2}\s+(?<header>[A-Za-z0-9_./\\-]+\.h)::(?<interface>[A-Za-z0-9_]+)"))
        {
            string header = HeaderName(match.Groups["header"].Value);
            if (match.Groups["interface"].Value.EndsWith("Internal", StringComparison.Ordinal))
            {
                internalHeaders.Add(header);
            }
            else
            {
                publicHeaders.Add(header);
            }
        }

        var catalogHeaders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(catalog, @"(?i)(?<header>[A-Za-z0-9_./\\-]+\.h)"))
        {
            string header = HeaderName(match.Groups["header"].Value);
            if (!internalHeaders.Contains(header) || publicHeaders.Contains(header))
            {
                catalogHeaders.Add(header);
            }
        }
        if (catalogHeaders.Count == 0)
        {
            throw new InvalidDataException("No interface headers were found in '" + guidPath + "'.");
        }

        var excludedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UI/Agent/IFolderArchive.h",
            "UI/Common/IFileExtractCallback.h",
            "UI/FileManager/PluginInterface.h"
        };
        var headerFiles = Directory.GetFiles(headersRoot, "*.h", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var partitions = new List<ITaskItem>();
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var partitionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string headerName in catalogHeaders)
        {
            if (!headerFiles.Contains(headerName))
            {
                throw new FileNotFoundException("Guid.txt references a header that was not found under '" + headersRoot + "': " + headerName);
            }
            foreach (string headerPath in headerFiles[headerName])
            {
                string relativeHeader = headerPath.Substring(headersRoot.Length + 1).Replace('\\', '/');
                if (excludedHeaders.Contains(relativeHeader))
                {
                    continue;
                }
                string headerNamespace;
                if (!namespaceMap["headers"].TryGetValue(relativeHeader, out headerNamespace))
                {
                    throw new InvalidDataException("Assign a functional namespace to '" + relativeHeader + "' in '" + NamespaceMapPath + "'.");
                }

                string partitionName = Regex.Replace(relativeHeader, "[^A-Za-z0-9_]", "_");
                if (!partitionNames.Add(partitionName))
                {
                    throw new InvalidDataException("Duplicate generated partition name: " + partitionName);
                }
                string partitionDirectory = Path.Combine(PartitionsRoot, partitionName);
                string relativePath = "7zip/" + relativeHeader;
                string mainPath = Path.Combine(partitionDirectory, "main.cpp");
                WriteIfChanged(Path.Combine(partitionDirectory, "settings.rsp"),
                    "--traverse\r\n<IncludeRoot>/" + relativePath + "\r\n--namespace\r\n" + headerNamespace + "\r\n");
                WriteIfChanged(mainPath, GeneratedComment + "\r\n#include \"" + relativePath.Replace('/', '\\') + "\"\r\n");

                var partition = new TaskItem(mainPath);
                partition.SetMetadata("Name", partitionName);
                partition.SetMetadata("TraverseFiles", headerPath);
                partitions.Add(partition);
                imports.Add("using " + headerNamespace + ";");
            }
        }

        WriteIfChanged(ScraperHeaderPath, GeneratedComment + "\r\n" + string.Join("\r\n", imports) + "\r\n");
        WriteIfChanged(NamespaceResponsePath, "--requiredNamespaceForName\r\n" + string.Join("\r\n",
            namespaceMap["types"].OrderBy(mapping => mapping.Key, StringComparer.Ordinal)
                .Select(mapping => mapping.Key + "=" + mapping.Value)) + "\r\n");
        Partitions = partitions.ToArray();
        Log.LogMessage(MessageImportance.High, "Prepared {0} header partitions from Guid.txt.", Partitions.Length);
    }

    private static string HeaderName(string path)
    {
        return Path.GetFileName(path.Replace('\\', '/'));
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, content, Utf8);
        }
    }
}
