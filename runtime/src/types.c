#include "oxygen_runtime.h"
#include <string.h>

/* Each class and interface has one type information record, emitted by the compiler: a type of a library is defined once, by the library,
 * so two types are the same type when their addresses are equal. A copy of a record, in another object, is the same type too when its name
 * is the same, since names are module-qualified and unique. The supertype names are emitted by the compiler, so this check follows no
 * relations and needs no registration at startup. */
uint8_t __oxygen_runtime_is_instance(
    const void *object,
    const oxygen_runtime_type_info *expected_type)
{
    if (object == NULL || expected_type == NULL)
        return 0;

    const oxygen_runtime_type_info *actual_type =
        *(const oxygen_runtime_type_info *const *)object;
    if (actual_type == NULL)
        return 0;
    if (actual_type == expected_type)
        return 1;
    for (uint32_t index = 0; index < actual_type->supertype_count; index++)
        if (strcmp(actual_type->supertype_names[index], expected_type->name) == 0)
            return 1;
    return 0;
}
