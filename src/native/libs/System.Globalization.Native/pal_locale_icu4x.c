// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ICU4X implementation of locale APIs for WASM.

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "pal_locale.h"

// ICU4X headers
#include "Locale.h"
#include "LocaleCanonicalizer.h"
#include "LocaleDirectionality.h"
#include "LocaleExpander.h"
#include "diplomat_runtime.h"

// Replaces underscore with hyphen in locale name (ICU uses _ but .NET uses -)
static void FixupLocaleName(char* name)
{
    for (char* p = name; *p; p++)
    {
        if (*p == '_')
            *p = '-';
    }
}

int32_t GlobalizationNative_GetLocaleName(const UChar* localeName, UChar* value, int32_t valueLength)
{
    // Convert UTF-16 locale to UTF-8
    char utf8Name[ULOC_FULLNAME_CAPACITY];
    int32_t i = 0;
    while (i < ULOC_FULLNAME_CAPACITY - 1 && localeName[i] != 0)
    {
        utf8Name[i] = (char)localeName[i]; // locale names are ASCII
        i++;
    }
    utf8Name[i] = 0;

    // Use ICU4X to canonicalize
    DiplomatStringView nameView = { utf8Name, (size_t)i };
    icu4x_Locale_from_string_mv1_result result = icu4x_Locale_from_string_mv1(nameView);

    char outputBuf[ULOC_FULLNAME_CAPACITY];
    int32_t outputLen = 0;

    if (result.is_ok)
    {
        DiplomatWrite write = diplomat_simple_write(outputBuf, sizeof(outputBuf));
        icu4x_Locale_to_string_mv1(result.ok, &write);
        outputLen = (int32_t)write.len;
        icu4x_Locale_destroy_mv1(result.ok);
    }
    else
    {
        // Fallback: copy input as-is
        strncpy(outputBuf, utf8Name, sizeof(outputBuf) - 1);
        outputLen = i;
    }

    FixupLocaleName(outputBuf);

    // Convert back to UTF-16
    int32_t j = 0;
    for (j = 0; j < outputLen && j < valueLength - 1; j++)
        value[j] = (UChar)(unsigned char)outputBuf[j];
    value[j] = 0;

    return j > 0 ? 1 : 0;
}

int32_t GlobalizationNative_GetDefaultLocaleName(UChar* value, int32_t valueLength)
{
    // On WASM, the default locale comes from the browser via JS.
    // Return empty string — the managed side handles the default.
    if (value && valueLength > 0)
        value[0] = 0;
    return 1;
}

int32_t GlobalizationNative_IsPredefinedLocale(const char* localeName)
{
    DiplomatStringView nameView = { localeName, strlen(localeName) };
    icu4x_Locale_from_string_mv1_result result = icu4x_Locale_from_string_mv1(nameView);
    if (result.is_ok)
    {
        icu4x_Locale_destroy_mv1(result.ok);
        return 1;
    }
    return 0;
}

int32_t GlobalizationNative_GetLocales(UChar* value, int32_t valueLength)
{
    // ICU4X with compiled_data doesn't enumerate available locales the same way.
    // Return empty — managed code handles locale discovery on WASM.
    (void)value;
    (void)valueLength;
    return 0;
}

int32_t GlobalizationNative_GetLocaleTimeFormat(const UChar* localeName, int32_t shortFormat,
                                                 UChar* value, int32_t valueLength)
{
    (void)localeName;
    // Return a basic time pattern — the managed layer has its own pattern handling
    const UChar* pattern = shortFormat ? u"HH:mm" : u"HH:mm:ss";
    int32_t len = 0;
    while (pattern[len]) len++;

    int32_t copyLen = len < valueLength - 1 ? len : valueLength - 1;
    memcpy(value, pattern, copyLen * sizeof(UChar));
    value[copyLen] = 0;
    return 1;
}

int32_t GlobalizationNative_GetCharacterOrientation(const UChar* localeName, UChar* value, int32_t valueLength)
{
    // Convert locale name
    char utf8Name[ULOC_FULLNAME_CAPACITY];
    int32_t i = 0;
    while (i < ULOC_FULLNAME_CAPACITY - 1 && localeName[i] != 0)
    {
        utf8Name[i] = (char)localeName[i];
        i++;
    }
    utf8Name[i] = 0;

    DiplomatStringView nameView = { utf8Name, (size_t)i };
    icu4x_Locale_from_string_mv1_result locResult = icu4x_Locale_from_string_mv1(nameView);

    const UChar* result = u"left-to-right";
    if (locResult.is_ok)
    {
        LocaleDirectionality* dir = icu4x_LocaleDirectionality_create_common_mv1();
        if (dir && icu4x_LocaleDirectionality_is_right_to_left_mv1(dir, locResult.ok))
        {
            result = u"right-to-left";
        }
        if (dir) icu4x_LocaleDirectionality_destroy_mv1(dir);
        icu4x_Locale_destroy_mv1(locResult.ok);
    }

    int32_t len = 0;
    while (result[len]) len++;
    int32_t copyLen = len < valueLength - 1 ? len : valueLength - 1;
    memcpy(value, result, copyLen * sizeof(UChar));
    value[copyLen] = 0;
    return 1;
}
