// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.WebAssembly.Binary;

namespace Microsoft.WebAssembly.Build.Tasks;

/// <summary>
/// Reports the data section size of each WebAssembly object file, replacing <c>llvm-size -d --format=sysv</c>
/// (whose <c>DATA</c> row is the data section's payload size). Files without a data section produce no item.
/// </summary>
public class WasmGetDataSectionSizes : Task
{
    [NotNull]
    [Required]
    public ITaskItem[]? ObjectFiles { get; set; }

    /// <summary>One item per object with a data section; the item spec is the size in bytes.</summary>
    [Output]
    public ITaskItem[] DataSectionSizes { get; set; } = [];

    public override bool Execute()
    {
        var sizes = new List<ITaskItem>();
        foreach (ITaskItem objectFile in ObjectFiles)
        {
            try
            {
                foreach (WasmSectionInfo section in WasmSections.Read(objectFile.ItemSpec))
                {
                    if (section.Id == WasmSectionInfo.DataSectionId)
                    {
                        var item = new TaskItem(section.PayloadSize.ToString(CultureInfo.InvariantCulture));
                        item.SetMetadata("ObjectFile", objectFile.ItemSpec);
                        sizes.Add(item);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or WasmFormatException)
            {
                Log.LogError($"Failed to read WebAssembly sections from '{objectFile.ItemSpec}': {ex.Message}");
            }
        }

        DataSectionSizes = sizes.ToArray();
        return !Log.HasLoggedErrors;
    }
}
