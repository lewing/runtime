// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of timezone APIs for WASM.
// On WASM, .NET uses MinimalGlobalizationData which stubs out all timezone
// display name queries. These functions exist to satisfy the linker but
// should never be called at runtime.

#include <stdint.h>
#include <string.h>

#include "pal_timeZoneInfo.h"

int32_t GlobalizationNative_WindowsIdToIanaId(
    const UChar* windowsId, const char* region, UChar* ianaId, int32_t ianaIdLength)
{
    (void)windowsId;
    (void)region;
    (void)ianaId;
    (void)ianaIdLength;
    // WASM uses MinimalGlobalizationData — this is never called from managed code.
    return 0;
}

int32_t GlobalizationNative_IanaIdToWindowsId(
    const UChar* ianaId, UChar* windowsId, int32_t windowsIdLength)
{
    (void)ianaId;
    (void)windowsId;
    (void)windowsIdLength;
    return 0;
}

ResultCode GlobalizationNative_GetTimeZoneDisplayName(
    const UChar* localeName, const UChar* timeZoneId,
    TimeZoneDisplayNameType type, UChar* result, int32_t resultLength)
{
    (void)localeName;
    (void)timeZoneId;
    (void)type;
    // Return empty string
    if (result && resultLength > 0)
        result[0] = 0;
    return Success;
}
