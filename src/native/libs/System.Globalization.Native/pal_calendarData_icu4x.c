// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of calendar data APIs for WASM.

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "pal_calendarData.h"

// ICU4X headers
#include "Locale.h"
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

// Copy an ASCII string into a UChar buffer.
static int32_t CopyAsciiToUChar(const char* src, UChar* dst, int32_t dstLen)
{
    int32_t i = 0;
    while (src[i] != 0 && i < dstLen - 1)
    {
        dst[i] = (UChar)(unsigned char)src[i];
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

// Check if locale string starts with prefix (case-insensitive).
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

// Parse locale and extract language and region via ICU4X.
static bool ParseLocaleComponents(const char* utf8Name, int32_t nameLen,
                                   char* lang, int32_t langLen,
                                   char* region, int32_t regionLen)
{
    DiplomatStringView nameView = { utf8Name, (size_t)nameLen };
    icu4x_Locale_from_string_mv1_result result = icu4x_Locale_from_string_mv1(nameView);
    if (!result.is_ok)
        return false;

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

    icu4x_Locale_destroy_mv1(result.ok);
    return true;
}

int32_t GlobalizationNative_GetCalendars(const UChar* localeName,
                                          CalendarId* calendars,
                                          int32_t calendarsCapacity)
{
    if (!calendars || calendarsCapacity <= 0)
        return 0;

    char utf8Name[ULOC_FULLNAME_CAPACITY];
    int32_t nameLen = UCharLocaleToUtf8(localeName, utf8Name, ULOC_FULLNAME_CAPACITY);

    char lang[32] = {0};
    char region[32] = {0};
    ParseLocaleComponents(utf8Name, nameLen, lang, sizeof(lang), region, sizeof(region));

    int32_t count = 0;

    // Japanese locale gets the Japanese calendar as primary, then Gregorian
    if (StrEqualCI(lang, "ja") && (StrEqualCI(region, "JP") || region[0] == 0))
    {
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
        if (count < calendarsCapacity)
            calendars[count++] = JAPAN;
    }
    // Taiwan locale
    else if (StrEqualCI(lang, "zh") && StrEqualCI(region, "TW"))
    {
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
        if (count < calendarsCapacity)
            calendars[count++] = TAIWAN;
    }
    // Korean locale
    else if (StrEqualCI(lang, "ko") && (StrEqualCI(region, "KR") || region[0] == 0))
    {
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
        if (count < calendarsCapacity)
            calendars[count++] = KOREA;
    }
    // Thai locale
    else if (StrEqualCI(lang, "th") && (StrEqualCI(region, "TH") || region[0] == 0))
    {
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
        if (count < calendarsCapacity)
            calendars[count++] = THAI;
    }
    // Arabic locales → Hijri calendar
    else if (StrEqualCI(lang, "ar"))
    {
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
        if (StrEqualCI(region, "SA"))
        {
            if (count < calendarsCapacity)
                calendars[count++] = UMALQURA;
        }
        else
        {
            if (count < calendarsCapacity)
                calendars[count++] = HIJRI;
        }
    }
    // Hebrew locale
    else if (StrEqualCI(lang, "he") || StrEqualCI(lang, "iw"))
    {
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
        if (count < calendarsCapacity)
            calendars[count++] = HEBREW;
    }
    // Persian locale
    else if (StrEqualCI(lang, "fa"))
    {
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
        if (count < calendarsCapacity)
            calendars[count++] = PERSIAN;
    }
    else
    {
        // Default: Gregorian only
        if (count < calendarsCapacity)
            calendars[count++] = GREGORIAN;
    }

    return count;
}

ResultCode GlobalizationNative_GetCalendarInfo(const UChar* localeName,
                                                CalendarId calendarId,
                                                CalendarDataType dataType,
                                                UChar* result,
                                                int32_t resultCapacity)
{
    if (!result || resultCapacity <= 0)
        return UnknownError;

    result[0] = 0;

    switch (dataType)
    {
        case CalendarData_NativeName:
        {
            switch (calendarId)
            {
                case GREGORIAN:
                case GREGORIAN_US:
                    CopyUCharLiteral(u"Gregorian Calendar", result, resultCapacity);
                    break;
                case JAPAN:
                    CopyUCharLiteral(u"Japanese Calendar", result, resultCapacity);
                    break;
                case TAIWAN:
                    CopyUCharLiteral(u"Taiwan Calendar", result, resultCapacity);
                    break;
                case KOREA:
                    CopyUCharLiteral(u"Korean Calendar", result, resultCapacity);
                    break;
                case HIJRI:
                    CopyUCharLiteral(u"Hijri Calendar", result, resultCapacity);
                    break;
                case THAI:
                    CopyUCharLiteral(u"Thai Buddhist Calendar", result, resultCapacity);
                    break;
                case HEBREW:
                    CopyUCharLiteral(u"Hebrew Calendar", result, resultCapacity);
                    break;
                case PERSIAN:
                    CopyUCharLiteral(u"Persian Calendar", result, resultCapacity);
                    break;
                case UMALQURA:
                    CopyUCharLiteral(u"Um Al Qura Calendar", result, resultCapacity);
                    break;
                default:
                    CopyUCharLiteral(u"Gregorian Calendar", result, resultCapacity);
                    break;
            }
            return Success;
        }

        case CalendarData_MonthDay:
            CopyUCharLiteral(u"MMMM dd", result, resultCapacity);
            return Success;

        case CalendarData_ShortDates:
            CopyUCharLiteral(u"M/d/yyyy", result, resultCapacity);
            return Success;

        case CalendarData_LongDates:
            CopyUCharLiteral(u"dddd, MMMM d, yyyy", result, resultCapacity);
            return Success;

        case CalendarData_YearMonths:
            CopyUCharLiteral(u"MMMM yyyy", result, resultCapacity);
            return Success;

        default:
            return UnknownError;
    }
}

// Invoke callback with a UChar string.
static void InvokeCallback(EnumCalendarInfoCallback callback, const void* context, const UChar* str)
{
    if (callback && str)
        callback(str, context);
}

int32_t GlobalizationNative_EnumCalendarInfo(EnumCalendarInfoCallback callback,
                                              const UChar* localeName,
                                              CalendarId calendarId,
                                              CalendarDataType dataType,
                                              const void* context)
{
    if (!callback)
        return 0;

    switch (dataType)
    {
        case CalendarData_ShortDates:
        {
            InvokeCallback(callback, context, u"M/d/yyyy");
            InvokeCallback(callback, context, u"M/d/yy");
            InvokeCallback(callback, context, u"yyyy-MM-dd");
            return 1;
        }

        case CalendarData_LongDates:
        {
            InvokeCallback(callback, context, u"dddd, MMMM d, yyyy");
            InvokeCallback(callback, context, u"MMMM d, yyyy");
            return 1;
        }

        case CalendarData_YearMonths:
        {
            InvokeCallback(callback, context, u"MMMM yyyy");
            InvokeCallback(callback, context, u"yyyy MMMM");
            return 1;
        }

        case CalendarData_DayNames:
        {
            InvokeCallback(callback, context, u"Sunday");
            InvokeCallback(callback, context, u"Monday");
            InvokeCallback(callback, context, u"Tuesday");
            InvokeCallback(callback, context, u"Wednesday");
            InvokeCallback(callback, context, u"Thursday");
            InvokeCallback(callback, context, u"Friday");
            InvokeCallback(callback, context, u"Saturday");
            return 1;
        }

        case CalendarData_AbbrevDayNames:
        {
            InvokeCallback(callback, context, u"Sun");
            InvokeCallback(callback, context, u"Mon");
            InvokeCallback(callback, context, u"Tue");
            InvokeCallback(callback, context, u"Wed");
            InvokeCallback(callback, context, u"Thu");
            InvokeCallback(callback, context, u"Fri");
            InvokeCallback(callback, context, u"Sat");
            return 1;
        }

        case CalendarData_SuperShortDayNames:
        {
            InvokeCallback(callback, context, u"Su");
            InvokeCallback(callback, context, u"Mo");
            InvokeCallback(callback, context, u"Tu");
            InvokeCallback(callback, context, u"We");
            InvokeCallback(callback, context, u"Th");
            InvokeCallback(callback, context, u"Fr");
            InvokeCallback(callback, context, u"Sa");
            return 1;
        }

        case CalendarData_MonthNames:
        case CalendarData_MonthGenitiveNames:
        {
            InvokeCallback(callback, context, u"January");
            InvokeCallback(callback, context, u"February");
            InvokeCallback(callback, context, u"March");
            InvokeCallback(callback, context, u"April");
            InvokeCallback(callback, context, u"May");
            InvokeCallback(callback, context, u"June");
            InvokeCallback(callback, context, u"July");
            InvokeCallback(callback, context, u"August");
            InvokeCallback(callback, context, u"September");
            InvokeCallback(callback, context, u"October");
            InvokeCallback(callback, context, u"November");
            InvokeCallback(callback, context, u"December");
            // 13th month (empty for Gregorian)
            InvokeCallback(callback, context, u"");
            return 1;
        }

        case CalendarData_AbbrevMonthNames:
        case CalendarData_AbbrevMonthGenitiveNames:
        {
            InvokeCallback(callback, context, u"Jan");
            InvokeCallback(callback, context, u"Feb");
            InvokeCallback(callback, context, u"Mar");
            InvokeCallback(callback, context, u"Apr");
            InvokeCallback(callback, context, u"May");
            InvokeCallback(callback, context, u"Jun");
            InvokeCallback(callback, context, u"Jul");
            InvokeCallback(callback, context, u"Aug");
            InvokeCallback(callback, context, u"Sep");
            InvokeCallback(callback, context, u"Oct");
            InvokeCallback(callback, context, u"Nov");
            InvokeCallback(callback, context, u"Dec");
            InvokeCallback(callback, context, u"");
            return 1;
        }

        case CalendarData_EraNames:
        {
            switch (calendarId)
            {
                case JAPAN:
                    InvokeCallback(callback, context, u"Meiji");
                    InvokeCallback(callback, context, u"Taisho");
                    InvokeCallback(callback, context, u"Showa");
                    InvokeCallback(callback, context, u"Heisei");
                    InvokeCallback(callback, context, u"Reiwa");
                    break;
                case HEBREW:
                    InvokeCallback(callback, context, u"Anno Mundi");
                    break;
                case HIJRI:
                case UMALQURA:
                    InvokeCallback(callback, context, u"Anno Hegirae");
                    break;
                case PERSIAN:
                    InvokeCallback(callback, context, u"Anno Persico");
                    break;
                case TAIWAN:
                    InvokeCallback(callback, context, u"Minguo");
                    break;
                case KOREA:
                    InvokeCallback(callback, context, u"Dangi");
                    break;
                case THAI:
                    InvokeCallback(callback, context, u"Buddhist Era");
                    break;
                default:
                    InvokeCallback(callback, context, u"B.C.");
                    InvokeCallback(callback, context, u"A.D.");
                    break;
            }
            return 1;
        }

        case CalendarData_AbbrevEraNames:
        {
            switch (calendarId)
            {
                case JAPAN:
                    InvokeCallback(callback, context, u"M");
                    InvokeCallback(callback, context, u"T");
                    InvokeCallback(callback, context, u"S");
                    InvokeCallback(callback, context, u"H");
                    InvokeCallback(callback, context, u"R");
                    break;
                case HEBREW:
                    InvokeCallback(callback, context, u"AM");
                    break;
                case HIJRI:
                case UMALQURA:
                    InvokeCallback(callback, context, u"AH");
                    break;
                case PERSIAN:
                    InvokeCallback(callback, context, u"AP");
                    break;
                case TAIWAN:
                    InvokeCallback(callback, context, u"ROC");
                    break;
                case KOREA:
                    InvokeCallback(callback, context, u"KE");
                    break;
                case THAI:
                    InvokeCallback(callback, context, u"BE");
                    break;
                default:
                    InvokeCallback(callback, context, u"BC");
                    InvokeCallback(callback, context, u"AD");
                    break;
            }
            return 1;
        }

        case CalendarData_MonthDay:
        {
            InvokeCallback(callback, context, u"MMMM dd");
            return 1;
        }

        default:
            return 0;
    }
}

int32_t GlobalizationNative_GetLatestJapaneseEra(void)
{
    // Era 5 = Reiwa (started May 1, 2019)
    return 5;
}

int32_t GlobalizationNative_GetJapaneseEraStartDate(int32_t era,
                                                     int32_t* startYear,
                                                     int32_t* startMonth,
                                                     int32_t* startDay)
{
    if (!startYear || !startMonth || !startDay)
        return 0;

    switch (era)
    {
        case 1: // Meiji
            *startYear = 1868;
            *startMonth = 1;
            *startDay = 1;
            return 1;

        case 2: // Taisho
            *startYear = 1912;
            *startMonth = 7;
            *startDay = 30;
            return 1;

        case 3: // Showa
            *startYear = 1926;
            *startMonth = 12;
            *startDay = 25;
            return 1;

        case 4: // Heisei
            *startYear = 1989;
            *startMonth = 1;
            *startDay = 8;
            return 1;

        case 5: // Reiwa
            *startYear = 2019;
            *startMonth = 5;
            *startDay = 1;
            return 1;

        default:
            *startYear = 0;
            *startMonth = 0;
            *startDay = 0;
            return 0;
    }
}
