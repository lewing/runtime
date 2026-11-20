// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace ILCompiler.PortableCallHelpers.WasmObject
{
    /// <summary>
    /// wasm32 layouts of the tables the runtime reads from the call-helper object, as callhelpers.hpp and
    /// minipal/entrypoints.h declare them. CallHelperLayoutTests checks them against those headers.
    /// </summary>
    internal static class CallHelperLayout
    {
        public const int PointerSize = 4;

        // StringToPortableSigThunk { const char* key; void* value; } and Entry { const char* name; const void* method; }
        public const int KeyValueSize = 2 * PointerSize;
        public const int KeyOffset = 0;
        public const int ValueOffset = PointerSize;

        // ReverseThunkMapEntry { ULONG hashCode; const char* Source; ReverseThunkMapValue { MethodDesc** Target; void* EntryPoint; } value; }
        public const int ReverseThunkEntrySize = 4 * PointerSize;
        public const int ReverseThunkHashOffset = 0;
        public const int ReverseThunkSourceOffset = PointerSize;
        public const int ReverseThunkTargetOffset = 2 * PointerSize;
        public const int ReverseThunkEntryPointOffset = 3 * PointerSize;

        // PInvokeTable { const char* LibraryName; const Entry* Entries; size_t EntryCount; }, defined in the generated
        // C++ and read only by the lookup function generated with it.
        public const int LibraryTableEntrySize = 3 * PointerSize;
    }
}
