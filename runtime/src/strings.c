#include <stdlib.h>
#include <string.h>
#include "oxygen_runtime.h"

const char *oxygen_string_to_c_string(oxygen_string value)
{
    for (uint64_t index = 0; index < value.length; index++)
    {
        if (value.data[index] == '\0')
            return value.data;
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
