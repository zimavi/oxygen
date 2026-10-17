#include <stdio.h>
#include <stdlib.h>
#include "oxygen_runtime.h"

void __oxygen_runtime_array_bounds_failure(uint64_t index, uint64_t length)
{
    fprintf(stderr, "Oxygen array index %llu is out of bounds for length %llu\n",
        (unsigned long long)index, (unsigned long long)length);
    fflush(stderr);
    abort();
}

void __oxygen_runtime_enum_cast_failure(uint64_t value)
{
    fprintf(stderr, "Oxygen value %llu is not a valid enum member\n",
        (unsigned long long)value);
    fflush(stderr);
    abort();
}

void __oxygen_runtime_invalid_cast(
    const oxygen_runtime_type_info *source_type,
    const oxygen_runtime_type_info *target_type)
{
    fprintf(stderr, "Oxygen cannot cast %s to %s\n",
        source_type == NULL || source_type->name == NULL ? "unknown" : source_type->name,
        target_type == NULL || target_type->name == NULL ? "unknown" : target_type->name);
    fflush(stderr);
    abort();
}
