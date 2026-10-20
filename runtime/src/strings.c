#include <stdlib.h>
#include <string.h>
#include "oxygen_runtime.h"

const char *oxygen_string_to_c_string(const oxygen_string *value)
{
    for (uint64_t index = 0; index < value->length; index++)
    {
        if (value->data[index] == '\0')
            return value->data;
    }

    char *result = malloc((size_t)value->length + 1);
    if (result == NULL)
        return NULL;

    if (value->length > 0)
        memcpy(result, value->data, (size_t)value->length);
    result[value->length] = '\0';
    return result;
}

/* The result is written through an out-pointer, as Oxygen passes string results of external functions. */
void oxygen_c_string_to_string(const char *value, oxygen_string *result)
{
    result->data = value;
    result->length = value != NULL ? (uint64_t)strlen((const char *)value) : 0;
}
