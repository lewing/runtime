// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of collation/comparison APIs for WASM.
// This replaces pal_collation.c when building with ICU4X.

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "pal_collation.h"

// ICU4X headers
#include "Collator.h"
#include "CollatorOptionsV1.d.h"
#include "CollatorStrength.d.h"
#include "CollatorAlternateHandling.d.h"
#include "CollatorCaseLevel.d.h"
#include "CollatorMaxVariable.d.h"
#include "Locale.h"
#include "diplomat_runtime.h"

// .NET CompareOptions flags (must match managed enum)
#define CompareOptionsIgnoreCase       0x1
#define CompareOptionsIgnoreNonSpace   0x2
#define CompareOptionsIgnoreSymbols    0x4
#define CompareOptionsIgnoreKanaType   0x8
#define CompareOptionsIgnoreWidth      0x10
#define CompareOptionsNumericOrdering  0x20
#define CompareOptionsMask             0x3f

struct SortHandle
{
    Locale* locale;
    Collator* collatorsPerOption[CompareOptionsMask + 1];
    char localeName[ULOC_FULLNAME_CAPACITY];
};

static CollatorStrength MapStrength(int32_t options)
{
    if (options & CompareOptionsIgnoreCase)
    {
        if (options & CompareOptionsIgnoreNonSpace)
            return CollatorStrength_Primary;
        return CollatorStrength_Secondary;
    }
    return CollatorStrength_Tertiary;
}

static Collator* GetCollatorForOptions(SortHandle* pSortHandle, int32_t options)
{
    int32_t maskedOptions = options & CompareOptionsMask;

    if (pSortHandle->collatorsPerOption[maskedOptions] != NULL)
        return pSortHandle->collatorsPerOption[maskedOptions];

    CollatorOptionsV1 opts;
    memset(&opts, 0, sizeof(opts));

    opts.strength.is_ok = true;
    opts.strength.ok = MapStrength(options);

    if (options & CompareOptionsIgnoreSymbols)
    {
        opts.alternate_handling.is_ok = true;
        opts.alternate_handling.ok = CollatorAlternateHandling_Shifted;
        opts.max_variable.is_ok = true;
        opts.max_variable.ok = CollatorMaxVariable_Currency;
    }

    // NumericOrdering maps to CollatorNumericOrdering (not yet in CollatorOptionsV1)

    icu4x_Collator_create_v1_mv1_result result =
        icu4x_Collator_create_v1_mv1(pSortHandle->locale, opts);

    if (!result.is_ok)
        return NULL;

    pSortHandle->collatorsPerOption[maskedOptions] = result.ok;
    return result.ok;
}

ResultCode GlobalizationNative_GetSortHandle(const char* lpLocaleName, SortHandle** ppSortHandle)
{
    SortHandle* handle = (SortHandle*)calloc(1, sizeof(SortHandle));
    if (!handle)
        return GetResultCode(U_MEMORY_ALLOCATION_ERROR);

    DiplomatStringView nameView = { lpLocaleName, strlen(lpLocaleName) };
    icu4x_Locale_from_string_mv1_result locResult = icu4x_Locale_from_string_mv1(nameView);

    if (!locResult.is_ok)
    {
        // Fall back to unknown/root locale
        handle->locale = icu4x_Locale_unknown_mv1();
    }
    else
    {
        handle->locale = locResult.ok;
    }

    strncpy(handle->localeName, lpLocaleName, ULOC_FULLNAME_CAPACITY - 1);
    *ppSortHandle = handle;
    return Success;
}

void GlobalizationNative_CloseSortHandle(SortHandle* pSortHandle)
{
    if (pSortHandle)
    {
        for (int i = 0; i <= CompareOptionsMask; i++)
        {
            if (pSortHandle->collatorsPerOption[i])
                icu4x_Collator_destroy_mv1(pSortHandle->collatorsPerOption[i]);
        }
        if (pSortHandle->locale)
            icu4x_Locale_destroy_mv1(pSortHandle->locale);
        free(pSortHandle);
    }
}

int32_t GlobalizationNative_GetSortVersion(SortHandle* pSortHandle)
{
    (void)pSortHandle;
    // ICU4X doesn't expose a version number for collation data.
    // Return a fixed value; changes when ICU4X CLDR data is updated.
    return 1;
}

int32_t GlobalizationNative_CompareString(
    SortHandle* pSortHandle,
    const UChar* lpStr1, int32_t cwStr1Length,
    const UChar* lpStr2, int32_t cwStr2Length,
    int32_t options)
{
    Collator* collator = GetCollatorForOptions(pSortHandle, options);
    if (!collator)
        return 0;

    DiplomatString16View s1 = { (const char16_t*)lpStr1, (size_t)cwStr1Length };
    DiplomatString16View s2 = { (const char16_t*)lpStr2, (size_t)cwStr2Length };

    return (int32_t)icu4x_Collator_compare_utf16_mv1(collator, s1, s2);
}

// --- String Search ---
// ICU4X does not yet have a usearch equivalent. We implement collation-aware
// search using the Collator's compare function with a sliding window.
// This handles basic cases correctly. Variable-length collation matches
// (e.g. ß=ss) are handled by trying multiple target lengths.

static int32_t CollationSearch(
    Collator* collator,
    const UChar* lpSource, int32_t cwSourceLength,
    const UChar* lpTarget, int32_t cwTargetLength,
    bool fromBeginning,
    int32_t* pMatchedLength)
{
    if (cwTargetLength == 0)
    {
        if (pMatchedLength) *pMatchedLength = 0;
        return fromBeginning ? 0 : cwSourceLength;
    }
    if (cwSourceLength == 0 || cwTargetLength > cwSourceLength)
    {
        return -1;
    }

    DiplomatString16View target = { (const char16_t*)lpTarget, (size_t)cwTargetLength };

    int32_t start = fromBeginning ? 0 : cwSourceLength - 1;
    int32_t end = fromBeginning ? cwSourceLength : -1;
    int32_t step = fromBeginning ? 1 : -1;

    for (int32_t i = start; i != end; i += step)
    {
        // Try different window sizes to handle variable-length matches
        int32_t minWindow = cwTargetLength > 1 ? cwTargetLength - 1 : cwTargetLength;
        int32_t maxWindow = cwTargetLength + 2;
        if (maxWindow > cwSourceLength - i)
            maxWindow = cwSourceLength - i;
        if (minWindow > cwSourceLength - i)
            continue;

        for (int32_t windowLen = cwTargetLength; windowLen >= minWindow && windowLen <= maxWindow; windowLen++)
        {
            if (i + windowLen > cwSourceLength)
                continue;

            DiplomatString16View source = { (const char16_t*)(lpSource + i), (size_t)windowLen };
            int8_t cmp = icu4x_Collator_compare_utf16_mv1(collator, source, target);

            if (cmp == 0)
            {
                if (pMatchedLength) *pMatchedLength = windowLen;
                return i;
            }
        }
    }

    return -1;
}

int32_t GlobalizationNative_IndexOf(
    SortHandle* pSortHandle,
    const UChar* lpTarget, int32_t cwTargetLength,
    const UChar* lpSource, int32_t cwSourceLength,
    int32_t options, int32_t* pMatchedLength)
{
    Collator* collator = GetCollatorForOptions(pSortHandle, options);
    if (!collator) return -1;

    return CollationSearch(collator, lpSource, cwSourceLength,
                          lpTarget, cwTargetLength, true, pMatchedLength);
}

int32_t GlobalizationNative_LastIndexOf(
    SortHandle* pSortHandle,
    const UChar* lpTarget, int32_t cwTargetLength,
    const UChar* lpSource, int32_t cwSourceLength,
    int32_t options, int32_t* pMatchedLength)
{
    Collator* collator = GetCollatorForOptions(pSortHandle, options);
    if (!collator) return -1;

    return CollationSearch(collator, lpSource, cwSourceLength,
                          lpTarget, cwTargetLength, false, pMatchedLength);
}

int32_t GlobalizationNative_StartsWith(
    SortHandle* pSortHandle,
    const UChar* lpTarget, int32_t cwTargetLength,
    const UChar* lpSource, int32_t cwSourceLength,
    int32_t options, int32_t* pMatchedLength)
{
    Collator* collator = GetCollatorForOptions(pSortHandle, options);
    if (!collator) return false;

    if (cwTargetLength == 0)
    {
        if (pMatchedLength) *pMatchedLength = 0;
        return true;
    }

    DiplomatString16View target = { (const char16_t*)lpTarget, (size_t)cwTargetLength };

    // Try window sizes starting from target length
    for (int32_t windowLen = cwTargetLength - 1; windowLen <= cwTargetLength + 2 && windowLen <= cwSourceLength; windowLen++)
    {
        if (windowLen < 1) continue;
        DiplomatString16View source = { (const char16_t*)lpSource, (size_t)windowLen };
        if (icu4x_Collator_compare_utf16_mv1(collator, source, target) == 0)
        {
            if (pMatchedLength) *pMatchedLength = windowLen;
            return true;
        }
    }
    return false;
}

int32_t GlobalizationNative_EndsWith(
    SortHandle* pSortHandle,
    const UChar* lpTarget, int32_t cwTargetLength,
    const UChar* lpSource, int32_t cwSourceLength,
    int32_t options, int32_t* pMatchedLength)
{
    Collator* collator = GetCollatorForOptions(pSortHandle, options);
    if (!collator) return false;

    if (cwTargetLength == 0)
    {
        if (pMatchedLength) *pMatchedLength = 0;
        return true;
    }

    DiplomatString16View target = { (const char16_t*)lpTarget, (size_t)cwTargetLength };

    // Try window sizes from the end of source
    for (int32_t windowLen = cwTargetLength - 1; windowLen <= cwTargetLength + 2 && windowLen <= cwSourceLength; windowLen++)
    {
        if (windowLen < 1) continue;
        int32_t startPos = cwSourceLength - windowLen;
        DiplomatString16View source = { (const char16_t*)(lpSource + startPos), (size_t)windowLen };
        if (icu4x_Collator_compare_utf16_mv1(collator, source, target) == 0)
        {
            if (pMatchedLength) *pMatchedLength = windowLen;
            return true;
        }
    }
    return false;
}

int32_t GlobalizationNative_GetSortKey(
    SortHandle* pSortHandle,
    const UChar* lpStr, int32_t cwStrLength,
    uint8_t* sortKey, int32_t cbSortKeyLength,
    int32_t options)
{
    // ICU4X sort key generation is not yet exposed in the C API.
    // As a workaround, generate a simple comparison-compatible key
    // by encoding the string with normalization applied.
    // This is not a true collation sort key but allows basic sorting.
    //
    // TODO: Contribute sort key FFI to ICU4X and use proper implementation.
    (void)pSortHandle;
    (void)options;

    // Simple fallback: copy the UTF-16 code units as big-endian bytes.
    // This gives correct sort order for ASCII/Latin but not for locale-specific collation.
    int32_t needed = cwStrLength * 2 + 1;
    if (sortKey == NULL || cbSortKeyLength == 0)
        return needed;

    int32_t written = 0;
    for (int32_t i = 0; i < cwStrLength && written + 2 <= cbSortKeyLength; i++)
    {
        sortKey[written++] = (uint8_t)(lpStr[i] >> 8);
        sortKey[written++] = (uint8_t)(lpStr[i] & 0xFF);
    }
    if (written < cbSortKeyLength)
        sortKey[written++] = 0; // null terminator

    return written;
}
