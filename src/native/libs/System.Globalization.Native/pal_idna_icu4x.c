// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of IDNA APIs for WASM.
// ICU4X does not yet expose IDNA in its C API. We implement a basic
// UTS46 mapping using the standard algorithm with ASCII fast-path.

#include <stdbool.h>
#include <stdint.h>
#include <string.h>

#include "pal_idna.h"

// Basic IDNA implementation: for most domains, ASCII passthrough works.
// Non-ASCII domains need proper UTS46 processing which would require
// either the `idna` Rust crate or ICU4X IDNA support.
// This is a minimal implementation for the WASM prototype.

int32_t GlobalizationNative_ToAscii(
    uint32_t flags, const UChar* lpSrc, int32_t cwSrcLength, UChar* lpDst, int32_t cwDstLength)
{
    (void)flags;

    // Fast path: if input is all ASCII, just copy
    bool allAscii = true;
    for (int32_t i = 0; i < cwSrcLength; i++)
    {
        if (lpSrc[i] > 0x7F)
        {
            allAscii = false;
            break;
        }
    }

    if (allAscii)
    {
        int32_t copyLen = cwSrcLength < cwDstLength ? cwSrcLength : cwDstLength;
        // Lowercase for DNS (simplified)
        for (int32_t i = 0; i < copyLen; i++)
        {
            UChar c = lpSrc[i];
            lpDst[i] = (c >= 'A' && c <= 'Z') ? (c + 32) : c;
        }
        return copyLen;
    }

    // Non-ASCII: would need full Punycode encoding.
    // For now, return 0 (failure) — the managed layer will handle the error.
    // TODO: Integrate idna Rust crate for proper UTS46 support.
    return 0;
}

int32_t GlobalizationNative_ToUnicode(
    uint32_t flags, const UChar* lpSrc, int32_t cwSrcLength, UChar* lpDst, int32_t cwDstLength)
{
    (void)flags;

    // Fast path: if no xn-- ACE prefix, just copy
    bool hasAce = false;
    for (int32_t i = 0; i + 3 < cwSrcLength; i++)
    {
        if (lpSrc[i] == 'x' && lpSrc[i+1] == 'n' && lpSrc[i+2] == '-' && lpSrc[i+3] == '-')
        {
            hasAce = true;
            break;
        }
    }

    if (!hasAce)
    {
        int32_t copyLen = cwSrcLength < cwDstLength ? cwSrcLength : cwDstLength;
        memcpy(lpDst, lpSrc, copyLen * sizeof(UChar));
        return copyLen;
    }

    // ACE-encoded domain: would need Punycode decoding.
    // Return 0 for now.
    // TODO: Integrate idna Rust crate.
    return 0;
}
