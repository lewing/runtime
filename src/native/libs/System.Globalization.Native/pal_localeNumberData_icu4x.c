// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of locale numeric data APIs for WASM.

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "pal_localeNumberData.h"

// ICU4X headers
#include "Locale.h"
#include "LocaleDirectionality.h"
#include "WeekInformation.h"
#include "Weekday.d.h"
#include "diplomat_runtime.h"

#ifndef ULOC_FULLNAME_CAPACITY
#define ULOC_FULLNAME_CAPACITY 157
#endif

// Convert UChar* locale name (ASCII-range) to char*.
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

// Case-insensitive ASCII comparison.
static bool StrEqualCI(const char* a, const char* b)
{
    while (*a && *b)
    {
        char ca = *a, cb = *b;
        if (ca >= 'A' && ca <= 'Z') ca += 32;
        if (cb >= 'A' && cb <= 'Z') cb += 32;
        if (ca != cb) return false;
        a++;
        b++;
    }
    return *a == *b;
}

// Check if a string starts with a prefix (case-insensitive).
static bool StartsWithCI(const char* str, const char* prefix)
{
    while (*prefix)
    {
        char cs = *str, cp = *prefix;
        if (cs >= 'A' && cs <= 'Z') cs += 32;
        if (cp >= 'A' && cp <= 'Z') cp += 32;
        if (cs != cp) return false;
        str++;
        prefix++;
    }
    return true;
}

// Parse locale into ICU4X Locale and extract language/region.
static Locale* ParseLocale(const char* utf8Name, int32_t nameLen, char* lang, int32_t langLen, char* region, int32_t regionLen)
{
    DiplomatStringView nameView = { utf8Name, (size_t)nameLen };
    icu4x_Locale_from_string_mv1_result result = icu4x_Locale_from_string_mv1(nameView);
    if (!result.is_ok)
        return NULL;

    if (lang && langLen > 0)
    {
        char buf[64];
        DiplomatWrite write = diplomat_simple_write(buf, sizeof(buf));
        icu4x_Locale_language_mv1(result.ok, &write);
        int32_t copyLen = (int32_t)write.len < langLen - 1 ? (int32_t)write.len : langLen - 1;
        memcpy(lang, buf, copyLen);
        lang[copyLen] = 0;
    }

    if (region && regionLen > 0)
    {
        char buf[64];
        DiplomatWrite write = diplomat_simple_write(buf, sizeof(buf));
        icu4x_Locale_region_mv1_result regResult = icu4x_Locale_region_mv1(result.ok, &write);
        if (regResult.is_ok)
        {
            int32_t copyLen = (int32_t)write.len < regionLen - 1 ? (int32_t)write.len : regionLen - 1;
            memcpy(region, buf, copyLen);
            region[copyLen] = 0;
        }
        else
        {
            region[0] = 0;
        }
    }

    return result.ok;
}

// Convert ICU4X Weekday enum (Monday=1..Sunday=7) to .NET day-of-week convention.
// .NET uses: 0=Monday, 1=Tuesday, ..., 6=Sunday
static int32_t WeekdayToDotNet(Weekday wd)
{
    // ICU4X: Monday=1, Tuesday=2, ..., Sunday=7
    // .NET:  Monday=0, Tuesday=1, ..., Sunday=6
    return (int32_t)wd - 1;
}

int32_t GlobalizationNative_GetLocaleInfoInt(const UChar* localeName,
                                              LocaleNumberData localeNumberData,
                                              int32_t* value)
{
    if (!localeName || !value)
        return 0;

    char utf8Name[ULOC_FULLNAME_CAPACITY];
    int32_t nameLen = UCharLocaleToUtf8(localeName, utf8Name, ULOC_FULLNAME_CAPACITY);

    char lang[32] = {0};
    char region[32] = {0};
    Locale* locale = ParseLocale(utf8Name, nameLen, lang, sizeof(lang), region, sizeof(region));

    switch (localeNumberData)
    {
        case LocaleNumber_MeasurementSystem:
        {
            // US, Liberia, and Myanmar use non-metric
            if (StrEqualCI(region, "US") || StrEqualCI(region, "LR") || StrEqualCI(region, "MM"))
                *value = 1; // US measurement
            else
                *value = 0; // Metric
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_FractionalDigitsCount:
        {
            // Most currencies use 2 decimal places
            // JPY, KRW, etc. use 0
            if (StrEqualCI(region, "JP") || StrEqualCI(region, "KR"))
                *value = 0;
            else
                *value = 2;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_NegativeNumberFormat:
        {
            // 1 = -n (most common)
            *value = 1;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_MonetaryFractionalDigitsCount:
        {
            if (StrEqualCI(region, "JP") || StrEqualCI(region, "KR"))
                *value = 0;
            else
                *value = 2;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_PositiveMonetaryNumberFormat:
        {
            // 0 = $n (prefix symbol)
            *value = 0;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_NegativeMonetaryNumberFormat:
        {
            // 1 = -$n
            *value = 1;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_FirstDayofWeek:
        {
            if (locale)
            {
                icu4x_WeekInformation_create_mv1_result weekResult =
                    icu4x_WeekInformation_create_mv1(locale);
                if (weekResult.is_ok)
                {
                    Weekday firstDay = icu4x_WeekInformation_first_weekday_mv1(weekResult.ok);
                    *value = WeekdayToDotNet(firstDay);
                    icu4x_WeekInformation_destroy_mv1(weekResult.ok);
                    icu4x_Locale_destroy_mv1(locale);
                    return 1;
                }
                icu4x_Locale_destroy_mv1(locale);
            }
            // Fallback: Sunday=6 for US/CA/JP, Monday=0 for most others
            if (StrEqualCI(region, "US") || StrEqualCI(region, "CA") ||
                StrEqualCI(region, "JP") || StrEqualCI(region, "PH") ||
                StrEqualCI(region, "TH"))
                *value = 6; // Sunday
            else
                *value = 0; // Monday
            return 1;
        }

        case LocaleNumber_FirstWeekOfYear:
        {
            // 0 = FirstDay rule is the most common
            *value = 0;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_ReadingLayout:
        {
            if (locale)
            {
                LocaleDirectionality* dir = icu4x_LocaleDirectionality_create_common_mv1();
                if (dir)
                {
                    if (icu4x_LocaleDirectionality_is_right_to_left_mv1(dir, locale))
                        *value = 1; // RTL
                    else
                        *value = 0; // LTR
                    icu4x_LocaleDirectionality_destroy_mv1(dir);
                    icu4x_Locale_destroy_mv1(locale);
                    return 1;
                }
                icu4x_Locale_destroy_mv1(locale);
            }
            // Fallback: known RTL languages
            if (StrEqualCI(lang, "ar") || StrEqualCI(lang, "he") ||
                StrEqualCI(lang, "fa") || StrEqualCI(lang, "ur") ||
                StrEqualCI(lang, "yi") || StrEqualCI(lang, "ps") ||
                StrEqualCI(lang, "dv") || StrEqualCI(lang, "ks") ||
                StrEqualCI(lang, "ku") || StrEqualCI(lang, "sd") ||
                StrEqualCI(lang, "ug"))
                *value = 1;
            else
                *value = 0;
            return 1;
        }

        case LocaleNumber_NegativePercentFormat:
        {
            // 0 = -n %
            *value = 0;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_PositivePercentFormat:
        {
            // 0 = n %
            *value = 0;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_Digit:
        {
            // Codepoint of native zero digit
            if (StrEqualCI(lang, "ar"))
                *value = 0x0660; // Arabic-Indic zero
            else if (StrEqualCI(lang, "fa"))
                *value = 0x06F0; // Extended Arabic-Indic zero
            else
                *value = 0x0030; // ASCII '0'
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_Monetary:
        {
            // Monetary decimal separator — not a number, but pal header includes it.
            // Return 0 as a sensible default (no special monetary value).
            *value = 0;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        case LocaleNumber_LanguageId:
        {
            // LCID is a Windows concept; return 0 for invariant/unknown
            *value = 0;
            if (locale) icu4x_Locale_destroy_mv1(locale);
            return 1;
        }

        default:
            if (locale) icu4x_Locale_destroy_mv1(locale);
            *value = 0;
            return 0;
    }
}

int32_t GlobalizationNative_GetLocaleInfoGroupingSizes(const UChar* localeName,
                                                        LocaleNumberData localeGroupingData,
                                                        int32_t* primaryGroupSize,
                                                        int32_t* secondaryGroupSize)
{
    if (!localeName || !primaryGroupSize || !secondaryGroupSize)
        return 0;

    char utf8Name[ULOC_FULLNAME_CAPACITY];
    UCharLocaleToUtf8(localeName, utf8Name, ULOC_FULLNAME_CAPACITY);

    // Indian locales (hi-IN, bn-IN, etc.) use primary=3, secondary=2
    // Most other locales use primary=3, secondary=3
    if (StartsWithCI(utf8Name, "hi") || StartsWithCI(utf8Name, "bn") ||
        StartsWithCI(utf8Name, "gu") || StartsWithCI(utf8Name, "kn") ||
        StartsWithCI(utf8Name, "ml") || StartsWithCI(utf8Name, "mr") ||
        StartsWithCI(utf8Name, "or") || StartsWithCI(utf8Name, "pa") ||
        StartsWithCI(utf8Name, "ta") || StartsWithCI(utf8Name, "te") ||
        StartsWithCI(utf8Name, "as") || StartsWithCI(utf8Name, "ne"))
    {
        // Verify it's actually an Indian locale (has -IN region or no region)
        char lang[32] = {0}, region[32] = {0};
        int32_t nameLen = (int32_t)strlen(utf8Name);
        Locale* locale = ParseLocale(utf8Name, nameLen, lang, sizeof(lang), region, sizeof(region));
        if (locale) icu4x_Locale_destroy_mv1(locale);

        if (region[0] == 0 || StrEqualCI(region, "IN"))
        {
            *primaryGroupSize = 3;
            *secondaryGroupSize = 2;
            return 1;
        }
    }

    *primaryGroupSize = 3;
    *secondaryGroupSize = 3;
    return 1;
}
