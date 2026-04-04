// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X initialization shim for WASM.
// Replaces pal_icushim_static.c — no dynamic loading needed since
// ICU4X is statically linked with compiled data baked in.

#include <stdint.h>
#include <stdbool.h>

#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#endif

#include "pal_icushim.h"

static int32_t isInitialized = 0;

// With ICU4X compiled_data, no data file loading is needed.
// The CLDR data is baked into the static library at compile time.

int32_t GlobalizationNative_LoadICU(void)
{
    isInitialized = 1;
    return 1; // success
}

void GlobalizationNative_InitICUFunctions(void* icuuc, void* icuin, const char* version, const char* suffix)
{
    (void)icuuc;
    (void)icuin;
    (void)version;
    (void)suffix;
    // No-op: ICU4X functions are statically linked
}

int32_t GlobalizationNative_GetICUVersion(void)
{
    // Return ICU4X version as a single int.
    // ICU4X 2.2.0 → 2020200
    return 2020200;
}

// WASM entry point for loading ICU data — no-op with ICU4X compiled_data
#ifdef __EMSCRIPTEN__
EMSCRIPTEN_KEEPALIVE
#endif
int32_t mono_wasm_load_icu_data(const void* pData)
{
    (void)pData;
    // With ICU4X compiled_data, we don't need external data files.
    // Accept the call gracefully and return success.
    isInitialized = 1;
    return 1;
}
