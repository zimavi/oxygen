#include "oxygen_runtime.h"

/* Each class and interface has one type information record in the program, emitted by the compiler, so two types are the same type
 * exactly when their addresses are equal. The supertype list is also emitted by the compiler, so this check follows no relations
 * and needs no registration at startup. */
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
        if (actual_type->supertypes[index] == expected_type)
            return 1;
    return 0;
}
