// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of case conversion APIs for WASM.
// Uses simple (non-locale) casing since the existing ICU4C code does character-level mapping.

#include <stdbool.h>
#include <stdint.h>

#include "pal_casing.h"

// ICU4X headers
#include "CaseMapper.h"
#include "diplomat_runtime.h"

// Simple case mapping: one codepoint at a time, no context needed.
// This matches the existing ICU4C behavior which uses u_toupper/u_tolower per codepoint.

void GlobalizationNative_ChangeCase(
    const UChar* lpSrc, int32_t cwSrcLength, UChar* lpDst, int32_t cwDstLength, int32_t bToUpper)
{
    int32_t srcIdx = 0, dstIdx = 0;

    while (srcIdx < cwSrcLength && dstIdx < cwDstLength)
    {
        uint32_t cp;
        // Decode UTF-16
        uint16_t hi = lpSrc[srcIdx++];
        if (hi >= 0xD800 && hi <= 0xDBFF && srcIdx < cwSrcLength)
        {
            uint16_t lo = lpSrc[srcIdx];
            if (lo >= 0xDC00 && lo <= 0xDFFF)
            {
                cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                srcIdx++;
            }
            else
            {
                cp = hi;
            }
        }
        else
        {
            cp = hi;
        }

        // Map case
        uint32_t mapped = bToUpper
            ? (uint32_t)icu4x_CaseMapper_simple_uppercase_with_compiled_data_mv1((char32_t)cp)
            : (uint32_t)icu4x_CaseMapper_simple_lowercase_with_compiled_data_mv1((char32_t)cp);

        // Encode UTF-16
        if (mapped < 0x10000)
        {
            lpDst[dstIdx++] = (UChar)mapped;
        }
        else if (dstIdx + 1 < cwDstLength)
        {
            mapped -= 0x10000;
            lpDst[dstIdx++] = (UChar)(0xD800 + (mapped >> 10));
            lpDst[dstIdx++] = (UChar)(0xDC00 + (mapped & 0x3FF));
        }
    }
}

void GlobalizationNative_ChangeCaseInvariant(
    const UChar* lpSrc, int32_t cwSrcLength, UChar* lpDst, int32_t cwDstLength, int32_t bToUpper)
{
    int32_t srcIdx = 0, dstIdx = 0;

    while (srcIdx < cwSrcLength && dstIdx < cwDstLength)
    {
        uint32_t cp;
        uint16_t hi = lpSrc[srcIdx++];
        if (hi >= 0xD800 && hi <= 0xDBFF && srcIdx < cwSrcLength)
        {
            uint16_t lo = lpSrc[srcIdx];
            if (lo >= 0xDC00 && lo <= 0xDFFF)
            {
                cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                srcIdx++;
            }
            else cp = hi;
        }
        else cp = hi;

        uint32_t mapped;
        if (bToUpper)
        {
            // Windows invariant: U+0131 (dotless i) stays as U+0131
            mapped = (cp == 0x0131) ? 0x0131
                : (uint32_t)icu4x_CaseMapper_simple_uppercase_with_compiled_data_mv1((char32_t)cp);
        }
        else
        {
            // Windows invariant: U+0130 (I with dot above) stays as U+0130
            mapped = (cp == 0x0130) ? 0x0130
                : (uint32_t)icu4x_CaseMapper_simple_lowercase_with_compiled_data_mv1((char32_t)cp);
        }

        if (mapped < 0x10000)
            lpDst[dstIdx++] = (UChar)mapped;
        else if (dstIdx + 1 < cwDstLength)
        {
            mapped -= 0x10000;
            lpDst[dstIdx++] = (UChar)(0xD800 + (mapped >> 10));
            lpDst[dstIdx++] = (UChar)(0xDC00 + (mapped & 0x3FF));
        }
    }
}

void GlobalizationNative_ChangeCaseTurkish(
    const UChar* lpSrc, int32_t cwSrcLength, UChar* lpDst, int32_t cwDstLength, int32_t bToUpper)
{
    int32_t srcIdx = 0, dstIdx = 0;

    while (srcIdx < cwSrcLength && dstIdx < cwDstLength)
    {
        uint32_t cp;
        uint16_t hi = lpSrc[srcIdx++];
        if (hi >= 0xD800 && hi <= 0xDBFF && srcIdx < cwSrcLength)
        {
            uint16_t lo = lpSrc[srcIdx];
            if (lo >= 0xDC00 && lo <= 0xDFFF)
            {
                cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                srcIdx++;
            }
            else cp = hi;
        }
        else cp = hi;

        uint32_t mapped;
        if (bToUpper)
        {
            // Turkish: i (U+0069) → İ (U+0130)
            mapped = (cp == 0x0069) ? 0x0130
                : (uint32_t)icu4x_CaseMapper_simple_uppercase_with_compiled_data_mv1((char32_t)cp);
        }
        else
        {
            // Turkish: I (U+0049) → ı (U+0131)
            mapped = (cp == 0x0049) ? 0x0131
                : (uint32_t)icu4x_CaseMapper_simple_lowercase_with_compiled_data_mv1((char32_t)cp);
        }

        if (mapped < 0x10000)
            lpDst[dstIdx++] = (UChar)mapped;
        else if (dstIdx + 1 < cwDstLength)
        {
            mapped -= 0x10000;
            lpDst[dstIdx++] = (UChar)(0xD800 + (mapped >> 10));
            lpDst[dstIdx++] = (UChar)(0xDC00 + (mapped & 0x3FF));
        }
    }
}
