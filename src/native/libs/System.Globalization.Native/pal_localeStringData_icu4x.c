// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of locale string data APIs for WASM.

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "pal_localeStringData.h"

// ICU4X headers
#include "Locale.h"
#include "LocaleDisplayNamesFormatter.h"
#include "DisplayNamesOptionsV1.d.h"
#include "DisplayNamesStyle.d.h"
#include "DisplayNamesFallback.d.h"
#include "LanguageDisplay.d.h"
#include "DecimalFormatter.h"
#include "diplomat_runtime.h"

#ifndef ULOC_FULLNAME_CAPACITY
#define ULOC_FULLNAME_CAPACITY 157
#endif

// Convert UChar* (UTF-16, but ASCII-range for locale names) to char*.
// Returns the length of the converted string.
static int32_t UCharLocaleToUtf8(const UChar* src, char* dst, int32_t dstLen)
{
    int32_t i = 0;
    while (i < dstLen - 1 && src[i] != 0)
    {
        dst[i] = (char)src[i];
        i++;
    }
    dst[i] = 0;
    return i;
}

// Copy a UTF-8 string into a UChar (UTF-16) buffer.
// Handles only BMP characters (sufficient for ASCII and most symbols).
static int32_t Utf8ToUCharBuf(const char* src, int32_t srcLen, UChar* dst, int32_t dstLen)
{
    int32_t si = 0, di = 0;
    while (si < srcLen && di < dstLen - 1)
    {
        unsigned char c = (unsigned char)src[si];
        if (c < 0x80)
        {
            dst[di++] = (UChar)c;
            si++;
        }
        else if (c < 0xE0 && si + 1 < srcLen)
        {
            dst[di++] = (UChar)(((c & 0x1F) << 6) | (src[si + 1] & 0x3F));
            si += 2;
        }
        else if (c < 0xF0 && si + 2 < srcLen)
        {
            dst[di++] = (UChar)(((c & 0x0F) << 12) | ((src[si + 1] & 0x3F) << 6) | (src[si + 2] & 0x3F));
            si += 3;
        }
        else
        {
            // 4-byte UTF-8 (supplementary) → surrogate pair
            if (c >= 0xF0 && si + 3 < srcLen && di + 1 < dstLen - 1)
            {
                uint32_t cp = ((c & 0x07) << 18) | ((src[si + 1] & 0x3F) << 12) |
                              ((src[si + 2] & 0x3F) << 6) | (src[si + 3] & 0x3F);
                cp -= 0x10000;
                dst[di++] = (UChar)(0xD800 + (cp >> 10));
                dst[di++] = (UChar)(0xDC00 + (cp & 0x3FF));
                si += 4;
            }
            else
            {
                dst[di++] = (UChar)'?';
                si++;
            }
        }
    }
    dst[di] = 0;
    return di;
}

// Copy a UChar string literal into destination buffer.
static int32_t CopyUCharLiteral(const UChar* src, UChar* dst, int32_t dstLen)
{
    int32_t i = 0;
    while (src[i] != 0 && i < dstLen - 1)
    {
        dst[i] = src[i];
        i++;
    }
    dst[i] = 0;
    return i;
}

// Check if a locale's language tag matches (case-insensitive ASCII).
static bool LangEquals(const char* lang, const char* target)
{
    while (*lang && *target)
    {
        char a = *lang, b = *target;
        if (a >= 'A' && a <= 'Z') a += 32;
        if (b >= 'A' && b <= 'Z') b += 32;
        if (a != b) return false;
        lang++;
        target++;
    }
    return *lang == *target;
}

// Check if a locale's region tag matches.
static bool RegionEquals(const char* region, const char* target)
{
    return LangEquals(region, target);
}

// Get number format symbols based on locale.
// Many locales use comma as decimal separator and period as grouping (or vice versa).
static void GetDecimalSeparator(const char* lang, const char* region, UChar* value, int32_t valueLength)
{
    // Locales that use comma as decimal separator
    if (LangEquals(lang, "de") || LangEquals(lang, "fr") || LangEquals(lang, "es") ||
        LangEquals(lang, "pt") || LangEquals(lang, "it") || LangEquals(lang, "nl") ||
        LangEquals(lang, "da") || LangEquals(lang, "nb") || LangEquals(lang, "nn") ||
        LangEquals(lang, "sv") || LangEquals(lang, "fi") || LangEquals(lang, "pl") ||
        LangEquals(lang, "cs") || LangEquals(lang, "sk") || LangEquals(lang, "hu") ||
        LangEquals(lang, "ro") || LangEquals(lang, "hr") || LangEquals(lang, "sl") ||
        LangEquals(lang, "bg") || LangEquals(lang, "el") || LangEquals(lang, "ru") ||
        LangEquals(lang, "uk") || LangEquals(lang, "tr") || LangEquals(lang, "id") ||
        LangEquals(lang, "vi") || LangEquals(lang, "ca") || LangEquals(lang, "gl"))
    {
        CopyUCharLiteral(u",", value, valueLength);
    }
    else if (LangEquals(lang, "ar") || LangEquals(lang, "fa"))
    {
        // Arabic decimal separator U+066B
        CopyUCharLiteral(u"\u066B", value, valueLength);
    }
    else
    {
        CopyUCharLiteral(u".", value, valueLength);
    }
}

static void GetThousandSeparator(const char* lang, const char* region, UChar* value, int32_t valueLength)
{
    if (LangEquals(lang, "de") || LangEquals(lang, "es") || LangEquals(lang, "pt") ||
        LangEquals(lang, "it") || LangEquals(lang, "nl") || LangEquals(lang, "pl") ||
        LangEquals(lang, "cs") || LangEquals(lang, "sk") || LangEquals(lang, "hu") ||
        LangEquals(lang, "ro") || LangEquals(lang, "hr") || LangEquals(lang, "sl") ||
        LangEquals(lang, "bg") || LangEquals(lang, "el") || LangEquals(lang, "ru") ||
        LangEquals(lang, "uk") || LangEquals(lang, "tr") || LangEquals(lang, "id") ||
        LangEquals(lang, "ca") || LangEquals(lang, "gl"))
    {
        CopyUCharLiteral(u".", value, valueLength);
    }
    else if (LangEquals(lang, "fr") || LangEquals(lang, "nb") || LangEquals(lang, "nn") ||
             LangEquals(lang, "sv") || LangEquals(lang, "fi") || LangEquals(lang, "da"))
    {
        // Narrow no-break space U+202F (common in French etc.)
        CopyUCharLiteral(u"\u202F", value, valueLength);
    }
    else if (LangEquals(lang, "ar") || LangEquals(lang, "fa"))
    {
        // Arabic thousands separator U+066C
        CopyUCharLiteral(u"\u066C", value, valueLength);
    }
    else
    {
        CopyUCharLiteral(u",", value, valueLength);
    }
}

// Return the currency symbol for a locale's region.
static void GetCurrencySymbol(const char* lang, const char* region, UChar* value, int32_t valueLength)
{
    if (RegionEquals(region, "US"))      CopyUCharLiteral(u"$", value, valueLength);
    else if (RegionEquals(region, "GB")) CopyUCharLiteral(u"\u00A3", value, valueLength); // £
    else if (RegionEquals(region, "JP")) CopyUCharLiteral(u"\u00A5", value, valueLength); // ¥
    else if (RegionEquals(region, "CN")) CopyUCharLiteral(u"\u00A5", value, valueLength); // ¥
    else if (RegionEquals(region, "KR")) CopyUCharLiteral(u"\u20A9", value, valueLength); // ₩
    else if (RegionEquals(region, "IN")) CopyUCharLiteral(u"\u20B9", value, valueLength); // ₹
    else if (RegionEquals(region, "RU")) CopyUCharLiteral(u"\u20BD", value, valueLength); // ₽
    else if (RegionEquals(region, "BR")) CopyUCharLiteral(u"R$", value, valueLength);
    else if (RegionEquals(region, "CH")) CopyUCharLiteral(u"CHF", value, valueLength);
    else if (RegionEquals(region, "AU") || RegionEquals(region, "CA") ||
             RegionEquals(region, "NZ") || RegionEquals(region, "SG") ||
             RegionEquals(region, "HK"))
        CopyUCharLiteral(u"$", value, valueLength);
    // Default to Euro for many European countries
    else if (RegionEquals(region, "DE") || RegionEquals(region, "FR") ||
             RegionEquals(region, "IT") || RegionEquals(region, "ES") ||
             RegionEquals(region, "NL") || RegionEquals(region, "PT") ||
             RegionEquals(region, "AT") || RegionEquals(region, "BE") ||
             RegionEquals(region, "FI") || RegionEquals(region, "IE") ||
             RegionEquals(region, "GR") || RegionEquals(region, "SK") ||
             RegionEquals(region, "SI") || RegionEquals(region, "EE") ||
             RegionEquals(region, "LV") || RegionEquals(region, "LT"))
        CopyUCharLiteral(u"\u20AC", value, valueLength); // €
    else
        CopyUCharLiteral(u"\u00A4", value, valueLength); // ¤ generic currency
}

// Return the ISO 4217 currency code for a locale's region.
static void GetIsoCurrencyCode(const char* lang, const char* region, UChar* value, int32_t valueLength)
{
    if (RegionEquals(region, "US"))      CopyUCharLiteral(u"USD", value, valueLength);
    else if (RegionEquals(region, "GB")) CopyUCharLiteral(u"GBP", value, valueLength);
    else if (RegionEquals(region, "JP")) CopyUCharLiteral(u"JPY", value, valueLength);
    else if (RegionEquals(region, "CN")) CopyUCharLiteral(u"CNY", value, valueLength);
    else if (RegionEquals(region, "KR")) CopyUCharLiteral(u"KRW", value, valueLength);
    else if (RegionEquals(region, "IN")) CopyUCharLiteral(u"INR", value, valueLength);
    else if (RegionEquals(region, "RU")) CopyUCharLiteral(u"RUB", value, valueLength);
    else if (RegionEquals(region, "BR")) CopyUCharLiteral(u"BRL", value, valueLength);
    else if (RegionEquals(region, "CH")) CopyUCharLiteral(u"CHF", value, valueLength);
    else if (RegionEquals(region, "AU")) CopyUCharLiteral(u"AUD", value, valueLength);
    else if (RegionEquals(region, "CA")) CopyUCharLiteral(u"CAD", value, valueLength);
    else if (RegionEquals(region, "DE") || RegionEquals(region, "FR") ||
             RegionEquals(region, "IT") || RegionEquals(region, "ES") ||
             RegionEquals(region, "NL") || RegionEquals(region, "PT") ||
             RegionEquals(region, "AT") || RegionEquals(region, "BE") ||
             RegionEquals(region, "FI") || RegionEquals(region, "IE") ||
             RegionEquals(region, "GR") || RegionEquals(region, "SK") ||
             RegionEquals(region, "SI") || RegionEquals(region, "EE") ||
             RegionEquals(region, "LV") || RegionEquals(region, "LT"))
        CopyUCharLiteral(u"EUR", value, valueLength);
    else
        CopyUCharLiteral(u"XXX", value, valueLength);
}

// Compute the parent locale name: strip the last subtag.
static void GetParentLocaleName(const char* utf8Name, UChar* value, int32_t valueLength)
{
    int32_t len = (int32_t)strlen(utf8Name);
    // Find last '-' or '_'
    int32_t lastSep = -1;
    for (int32_t i = 0; i < len; i++)
    {
        if (utf8Name[i] == '-' || utf8Name[i] == '_')
            lastSep = i;
    }

    if (lastSep <= 0)
    {
        // No parent (root locale)
        if (valueLength > 0)
            value[0] = 0;
        return;
    }

    int32_t copyLen = lastSep < valueLength - 1 ? lastSep : valueLength - 1;
    for (int32_t i = 0; i < copyLen; i++)
    {
        char c = utf8Name[i];
        // Normalize underscore to hyphen
        value[i] = (UChar)(c == '_' ? '-' : c);
    }
    value[copyLen] = 0;
}

// Use ICU4X LocaleDisplayNamesFormatter to get a display name for a locale.
static int32_t GetDisplayName(const UChar* localeName, const UChar* uiLocaleName, UChar* value, int32_t valueLength)
{
    char utf8Locale[ULOC_FULLNAME_CAPACITY];
    int32_t locLen = UCharLocaleToUtf8(localeName, utf8Locale, ULOC_FULLNAME_CAPACITY);

    char utf8UiLocale[ULOC_FULLNAME_CAPACITY];
    int32_t uiLen = 0;
    if (uiLocaleName && uiLocaleName[0] != 0)
        uiLen = UCharLocaleToUtf8(uiLocaleName, utf8UiLocale, ULOC_FULLNAME_CAPACITY);
    else
    {
        // Default to "en" if no UI locale specified
        strcpy(utf8UiLocale, "en");
        uiLen = 2;
    }

    DiplomatStringView uiView = { utf8UiLocale, (size_t)uiLen };
    icu4x_Locale_from_string_mv1_result uiResult = icu4x_Locale_from_string_mv1(uiView);
    if (!uiResult.is_ok)
        return 0;

    DiplomatStringView locView = { utf8Locale, (size_t)locLen };
    icu4x_Locale_from_string_mv1_result locResult = icu4x_Locale_from_string_mv1(locView);
    if (!locResult.is_ok)
    {
        icu4x_Locale_destroy_mv1(uiResult.ok);
        return 0;
    }

    DisplayNamesOptionsV1 options;
    memset(&options, 0, sizeof(options));
    // Use defaults (no option overrides)

    icu4x_LocaleDisplayNamesFormatter_create_v1_mv1_result fmtResult =
        icu4x_LocaleDisplayNamesFormatter_create_v1_mv1(uiResult.ok, options);

    int32_t written = 0;
    if (fmtResult.is_ok)
    {
        char outputBuf[256];
        DiplomatWrite write = diplomat_simple_write(outputBuf, sizeof(outputBuf));
        icu4x_LocaleDisplayNamesFormatter_of_mv1(fmtResult.ok, locResult.ok, &write);
        written = Utf8ToUCharBuf(outputBuf, (int32_t)write.len, value, valueLength);
        icu4x_LocaleDisplayNamesFormatter_destroy_mv1(fmtResult.ok);
    }

    icu4x_Locale_destroy_mv1(locResult.ok);
    icu4x_Locale_destroy_mv1(uiResult.ok);
    return written > 0 ? 1 : 0;
}

int32_t GlobalizationNative_GetLocaleInfoString(const UChar* localeName,
                                                 LocaleStringData localeStringData,
                                                 UChar* value,
                                                 int32_t valueLength,
                                                 const UChar* uiLocaleName)
{
    if (!localeName || !value || valueLength <= 0)
        return 0;

    value[0] = 0;

    char utf8Name[ULOC_FULLNAME_CAPACITY];
    int32_t nameLen = UCharLocaleToUtf8(localeName, utf8Name, ULOC_FULLNAME_CAPACITY);

    // Parse locale to extract language and region
    DiplomatStringView nameView = { utf8Name, (size_t)nameLen };
    icu4x_Locale_from_string_mv1_result locResult = icu4x_Locale_from_string_mv1(nameView);

    char lang[32] = {0};
    char region[32] = {0};

    if (locResult.is_ok)
    {
        char buf[64];
        DiplomatWrite write = diplomat_simple_write(buf, sizeof(buf));
        icu4x_Locale_language_mv1(locResult.ok, &write);
        if (write.len < sizeof(lang))
        {
            memcpy(lang, buf, write.len);
            lang[write.len] = 0;
        }

        write = diplomat_simple_write(buf, sizeof(buf));
        icu4x_Locale_region_mv1_result regResult = icu4x_Locale_region_mv1(locResult.ok, &write);
        if (regResult.is_ok && write.len < sizeof(region))
        {
            memcpy(region, buf, write.len);
            region[write.len] = 0;
        }
        icu4x_Locale_destroy_mv1(locResult.ok);
    }
    else if (nameLen == 0)
    {
        // Empty locale → invariant / root
        strcpy(lang, "en");
    }

    switch (localeStringData)
    {
        case LocaleString_DecimalSeparator:
        case LocaleString_MonetaryDecimalSeparator:
            GetDecimalSeparator(lang, region, value, valueLength);
            return 1;

        case LocaleString_ThousandSeparator:
        case LocaleString_MonetaryThousandSeparator:
            GetThousandSeparator(lang, region, value, valueLength);
            return 1;

        case LocaleString_Digits:
        {
            // Native digit characters starting from '0'
            if (LangEquals(lang, "ar") || LangEquals(lang, "fa"))
            {
                // Arabic-Indic digits U+0660..U+0669 or Extended U+06F0..U+06F9
                UChar base = LangEquals(lang, "fa") ? 0x06F0 : 0x0660;
                int32_t i;
                for (i = 0; i < 10 && i < valueLength - 1; i++)
                    value[i] = (UChar)(base + i);
                value[i] = 0;
            }
            else
            {
                // ASCII digits
                int32_t i;
                for (i = 0; i < 10 && i < valueLength - 1; i++)
                    value[i] = (UChar)('0' + i);
                value[i] = 0;
            }
            return 1;
        }

        case LocaleString_MonetarySymbol:
            GetCurrencySymbol(lang, region, value, valueLength);
            return 1;

        case LocaleString_Iso4217MonetarySymbol:
            GetIsoCurrencyCode(lang, region, value, valueLength);
            return 1;

        case LocaleString_CurrencyEnglishName:
        case LocaleString_CurrencyNativeName:
        {
            // Return the ISO code as a reasonable fallback
            GetIsoCurrencyCode(lang, region, value, valueLength);
            return 1;
        }

        case LocaleString_AMDesignator:
            CopyUCharLiteral(u"AM", value, valueLength);
            return 1;

        case LocaleString_PMDesignator:
            CopyUCharLiteral(u"PM", value, valueLength);
            return 1;

        case LocaleString_PositiveSign:
            CopyUCharLiteral(u"+", value, valueLength);
            return 1;

        case LocaleString_NegativeSign:
            CopyUCharLiteral(u"-", value, valueLength);
            return 1;

        case LocaleString_Iso639LanguageTwoLetterName:
        {
            Utf8ToUCharBuf(lang, (int32_t)strlen(lang), value, valueLength);
            return 1;
        }

        case LocaleString_Iso639LanguageThreeLetterName:
        {
            // For the prototype, return the 2-letter code as a fallback
            Utf8ToUCharBuf(lang, (int32_t)strlen(lang), value, valueLength);
            return 1;
        }

        case LocaleString_Iso3166CountryName:
        case LocaleString_Iso3166CountryName2:
        {
            Utf8ToUCharBuf(region, (int32_t)strlen(region), value, valueLength);
            return 1;
        }

        case LocaleString_NaNSymbol:
            CopyUCharLiteral(u"NaN", value, valueLength);
            return 1;

        case LocaleString_PositiveInfinitySymbol:
            CopyUCharLiteral(u"\u221E", value, valueLength); // ∞
            return 1;

        case LocaleString_NegativeInfinitySymbol:
            CopyUCharLiteral(u"-\u221E", value, valueLength); // -∞
            return 1;

        case LocaleString_ParentName:
            GetParentLocaleName(utf8Name, value, valueLength);
            return 1;

        case LocaleString_PercentSymbol:
            CopyUCharLiteral(u"%", value, valueLength);
            return 1;

        case LocaleString_PerMilleSymbol:
            CopyUCharLiteral(u"\u2030", value, valueLength); // ‰
            return 1;

        case LocaleString_LocalizedDisplayName:
        case LocaleString_EnglishDisplayName:
        case LocaleString_NativeDisplayName:
        case LocaleString_LocalizedLanguageName:
        case LocaleString_EnglishLanguageName:
        case LocaleString_NativeLanguageName:
        case LocaleString_EnglishCountryName:
        case LocaleString_NativeCountryName:
            return GetDisplayName(localeName, uiLocaleName, value, valueLength);

        default:
            return 0;
    }
}
