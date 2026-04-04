// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of normalization APIs for WASM.

#include <stdbool.h>
#include <stdint.h>
#include <string.h>

#include "pal_normalization.h"

// ICU4X headers
#include "ComposingNormalizer.h"
#include "DecomposingNormalizer.h"
#include "diplomat_runtime.h"

// Lazy-initialized normalizer instances (ICU4X normalizers are stateless/reentrant)
static ComposingNormalizer* s_nfc = NULL;
static ComposingNormalizer* s_nfkc = NULL;
static DecomposingNormalizer* s_nfd = NULL;
static DecomposingNormalizer* s_nfkd = NULL;

static void EnsureNormalizersInitialized(void)
{
    if (s_nfc == NULL)
    {
        s_nfc = icu4x_ComposingNormalizer_create_nfc_mv1();
        s_nfkc = icu4x_ComposingNormalizer_create_nfkc_mv1();
        s_nfd = icu4x_DecomposingNormalizer_create_nfd_mv1();
        s_nfkd = icu4x_DecomposingNormalizer_create_nfkd_mv1();
    }
}

int32_t GlobalizationNative_IsNormalized(
    NormalizationForm normalizationForm, const UChar* lpStr, int32_t cwStrLength)
{
    EnsureNormalizersInitialized();

    DiplomatString16View input = { (const char16_t*)lpStr, (size_t)cwStrLength };
    bool result;

    switch (normalizationForm)
    {
        case FormC:
            result = icu4x_ComposingNormalizer_is_normalized_utf16_mv1(s_nfc, input);
            break;
        case FormD:
            result = icu4x_DecomposingNormalizer_is_normalized_utf16_mv1(s_nfd, input);
            break;
        case FormKC:
            result = icu4x_ComposingNormalizer_is_normalized_utf16_mv1(s_nfkc, input);
            break;
        case FormKD:
            result = icu4x_DecomposingNormalizer_is_normalized_utf16_mv1(s_nfkd, input);
            break;
        default:
            return -1;
    }

    return result ? 1 : 0;
}

// Helper: convert UTF-16 to UTF-8 into a stack/heap buffer
static int32_t Utf16ToUtf8(const UChar* src, int32_t srcLen, char* dst, int32_t dstCap)
{
    int32_t di = 0;
    for (int32_t si = 0; si < srcLen && di < dstCap; si++)
    {
        uint32_t cp = src[si];
        // Handle surrogate pairs
        if (cp >= 0xD800 && cp <= 0xDBFF && si + 1 < srcLen)
        {
            uint32_t lo = src[si + 1];
            if (lo >= 0xDC00 && lo <= 0xDFFF)
            {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                si++;
            }
        }

        if (cp < 0x80)
        {
            dst[di++] = (char)cp;
        }
        else if (cp < 0x800)
        {
            if (di + 2 > dstCap) break;
            dst[di++] = (char)(0xC0 | (cp >> 6));
            dst[di++] = (char)(0x80 | (cp & 0x3F));
        }
        else if (cp < 0x10000)
        {
            if (di + 3 > dstCap) break;
            dst[di++] = (char)(0xE0 | (cp >> 12));
            dst[di++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            dst[di++] = (char)(0x80 | (cp & 0x3F));
        }
        else
        {
            if (di + 4 > dstCap) break;
            dst[di++] = (char)(0xF0 | (cp >> 18));
            dst[di++] = (char)(0x80 | ((cp >> 12) & 0x3F));
            dst[di++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            dst[di++] = (char)(0x80 | (cp & 0x3F));
        }
    }
    return di;
}

// Helper: convert UTF-8 to UTF-16
static int32_t Utf8ToUtf16(const char* src, int32_t srcLen, UChar* dst, int32_t dstCap)
{
    int32_t di = 0;
    for (int32_t si = 0; si < srcLen && di < dstCap;)
    {
        uint32_t cp;
        uint8_t b = (uint8_t)src[si];
        if (b < 0x80)
        {
            cp = b; si += 1;
        }
        else if (b < 0xE0)
        {
            if (si + 1 >= srcLen) break;
            cp = ((b & 0x1F) << 6) | (src[si+1] & 0x3F);
            si += 2;
        }
        else if (b < 0xF0)
        {
            if (si + 2 >= srcLen) break;
            cp = ((b & 0x0F) << 12) | ((src[si+1] & 0x3F) << 6) | (src[si+2] & 0x3F);
            si += 3;
        }
        else
        {
            if (si + 3 >= srcLen) break;
            cp = ((b & 0x07) << 18) | ((src[si+1] & 0x3F) << 12) | ((src[si+2] & 0x3F) << 6) | (src[si+3] & 0x3F);
            si += 4;
        }

        if (cp < 0x10000)
        {
            dst[di++] = (UChar)cp;
        }
        else if (di + 1 < dstCap)
        {
            cp -= 0x10000;
            dst[di++] = (UChar)(0xD800 + (cp >> 10));
            dst[di++] = (UChar)(0xDC00 + (cp & 0x3FF));
        }
        else break;
    }
    return di;
}

int32_t GlobalizationNative_NormalizeString(
    NormalizationForm normalizationForm, const UChar* lpSrc, int32_t cwSrcLength, UChar* lpDst, int32_t cwDstLength)
{
    EnsureNormalizersInitialized();

    // ICU4X normalize works on UTF-8, so we convert UTF-16 → UTF-8 → normalize → UTF-8 → UTF-16
    // For most strings this is efficient since they're short.
    int32_t utf8Cap = cwSrcLength * 4 + 4;
    char utf8Buf[4096];
    char* utf8Src = (utf8Cap <= 4096) ? utf8Buf : (char*)malloc(utf8Cap);
    if (!utf8Src) return 0;

    int32_t utf8Len = Utf16ToUtf8(lpSrc, cwSrcLength, utf8Src, utf8Cap);
    DiplomatStringView input = { utf8Src, (size_t)utf8Len };

    // Normalize into a diplomat buffer
    DiplomatWrite* write = diplomat_buffer_write_create(utf8Cap);
    if (!write)
    {
        if (utf8Src != utf8Buf) free(utf8Src);
        return 0;
    }

    switch (normalizationForm)
    {
        case FormC:
            icu4x_ComposingNormalizer_normalize_mv1(s_nfc, input, write);
            break;
        case FormD:
            icu4x_DecomposingNormalizer_normalize_mv1(s_nfd, input, write);
            break;
        case FormKC:
            icu4x_ComposingNormalizer_normalize_mv1(s_nfkc, input, write);
            break;
        case FormKD:
            icu4x_DecomposingNormalizer_normalize_mv1(s_nfkd, input, write);
            break;
        default:
            diplomat_buffer_write_destroy(write);
            if (utf8Src != utf8Buf) free(utf8Src);
            return 0;
    }

    // Convert normalized UTF-8 back to UTF-16
    const char* normalizedUtf8 = diplomat_buffer_write_get_bytes(write);
    size_t normalizedUtf8Len = diplomat_buffer_write_len(write);

    int32_t result = Utf8ToUtf16(normalizedUtf8, (int32_t)normalizedUtf8Len, lpDst, cwDstLength);

    diplomat_buffer_write_destroy(write);
    if (utf8Src != utf8Buf) free(utf8Src);

    return result;
}
