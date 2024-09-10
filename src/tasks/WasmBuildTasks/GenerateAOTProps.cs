// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using JoinedString;

namespace Microsoft.WebAssembly.Build.Tasks
{
    public class GenerateAOTProps : Task
    {
        [NotNull]
        [Required]
        public ITaskItem2[]? Properties { get; set; }

        public ITaskItem2[] Items { get; set; } = Array.Empty<ITaskItem2>();

        [NotNull]
        [Required]
        public string? OutputFile { get; set; }

        private const string s_originalItemNameMetadata = "OriginalItemName__";
        private const string s_conditionToUseMetadata = "ConditionToUse__";
        private static readonly HashSet<string> s_metadataNamesToSkip = new()
        {
            "FullPath",
            "RootDir",
            "Filename",
            "Extension",
            "RelativeDir",
            "Directory",
            "RecursiveDir",
            "Identity",
            "ModifiedTime",
            "CreatedTime",
            "AccessedTime",
            "DefiningProjectFullPath",
            "DefiningProjectDirectory",
            "DefiningProjectName",
            "DefiningProjectExtension",
            s_originalItemNameMetadata,
            s_conditionToUseMetadata
        };

        public override bool Execute()
        {
            var outDir = Path.GetDirectoryName(OutputFile);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            string GetCondition(ITaskItem item)
                => item.GetMetadata(s_conditionToUseMetadata) switch {
                    string condition when !string.IsNullOrEmpty(condition) => $" Condition=\"{condition}\"",
                    _ => ""
                };

            string GetOriginalName(ITaskItem item)
            {
                string name = item.GetMetadata(s_originalItemNameMetadata);
                if (string.IsNullOrEmpty(name))
                {
                    Log.LogError($"Item {item} is missing {s_originalItemNameMetadata} metadata, for the item name");
                }
                return name;
            }

            bool Skip(string mdName) => s_metadataNamesToSkip.Contains(mdName);

            var writer = new JoinedStringStreamWriter(OutputFile, false);
            writer.Write(
                $$""""
                <Project>
                    <PropertyGroup>
                {{Properties.Join("", prop =>
                $"        <{prop.GetMetadata("Name")}{GetCondition(prop)}>{prop.EvaluatedIncludeEscaped}</{prop.GetMetadata("Name")}>{writer.NewLine}"
                )}}
                    </PropertyGroup>
                    <ItemGroup>
                {{Items.Join("", item =>
                $$$"""
                        <{{{GetOriginalName(item)}}} Include="{{{item.EvaluatedIncludeEscaped}}}"{{{GetCondition(item)}}}{{{writer.NewLine}}}{{{
                            item.MetadataNames.OfType<string>().Where(m => !Skip(m)).Join(writer.NewLine, metadataName =>
                $"            {metadataName}=\"{item.GetMetadataValueEscaped(metadataName)}\"")}}} />
                """
                )}}
                    </ItemGroup>
                </Project>
                """");

            return !Log.HasLoggedErrors;
        }
    }
}
