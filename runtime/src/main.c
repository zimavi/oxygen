#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "oxygen_runtime.h"

void __oxygen_runtime_initialize(void)
{
}

static void oxygen_write(FILE *stream, const oxygen_string *value)
{
    if (value == NULL || value->data == NULL || value->length == 0)
        return;

    fwrite(value->data, 1, (size_t)value->length, stream);
    fflush(stream);
}

void oxygen_write_stdout(const oxygen_string *value)
{
    oxygen_write(stdout, value);
}

void oxygen_write_stderr(const oxygen_string *value)
{
    oxygen_write(stderr, value);
}

const char *oxygen_string_to_c_string(oxygen_string value)
{
    for (uint64_t index = 0; index < value.length; index++)
    {
        if (value.data[index] == '\0')
            return NULL;
    }

    char *result = malloc((size_t)value.length + 1);
    if (result == NULL)
        return NULL;

    if (value.length > 0)
        memcpy(result, value.data, (size_t)value.length);
    result[value.length] = '\0';
    return result;
}

oxygen_string oxygen_c_string_to_string(const char *value)
{
    oxygen_string result = { value, 0 };
    if (value != NULL)
        result.length = (uint64_t)strlen((const char *)value);
    return result;
}