#include <stdio.h>
#include <stdlib.h>
#include "oxygen_runtime.h"

/* Ends the program after a runtime failure. Every failure that the runtime detects goes through here, so each one exits with the same
 * code, 101. The message has already been written to oxygen_runtime_error_stream() by the caller. abort() is not used: it exits with a different code on each
 * platform, and it can open a dialog on Windows. */
_Noreturn void oxygen_runtime_abort(void)
{
    fflush(stdout);
    fflush(stderr);
    _Exit(101);
}

void __oxygen_runtime_array_bounds_failure(uint64_t index, uint64_t length)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen array index %llu is out of bounds for length %llu\n",
        (unsigned long long)index, (unsigned long long)length);
    fflush(stderr);
    oxygen_runtime_abort();
}

void __oxygen_runtime_string_bounds_failure(uint64_t index, uint64_t length)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen string index %llu is out of bounds for length %llu\n",
        (unsigned long long)index, (unsigned long long)length);
    fflush(stderr);
    oxygen_runtime_abort();
}

void __oxygen_runtime_enum_cast_failure(uint64_t value)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen value %llu is not a valid enum member\n",
        (unsigned long long)value);
    fflush(stderr);
    oxygen_runtime_abort();
}

/* A null class reference was used in a program built with --no-std, which has no NullReferenceException to throw. Like the other runtime
 * failures, it stops the program with exit code 101. */
void __oxygen_runtime_null_reference_failure(void)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen stopped: a null class reference was used\n");
    fflush(stderr);
    oxygen_runtime_abort();
}

void __oxygen_runtime_arithmetic_failure(int32_t kind, uint64_t operand)
{
    switch (kind)
    {
    case 1:
        fprintf(oxygen_runtime_error_stream(), "Oxygen integer division by zero\n");
        break;
    case 2:
        fprintf(oxygen_runtime_error_stream(), "Oxygen signed integer division overflowed (minimum value divided by -1)\n");
        break;
    case 3:
        fprintf(oxygen_runtime_error_stream(), "Oxygen shift count %llu is out of range for the shifted integer type\n",
            (unsigned long long)operand);
        break;
    default:
        fprintf(oxygen_runtime_error_stream(), "Oxygen arithmetic failure %d\n", (int)kind);
        break;
    }
    fflush(stderr);
    oxygen_runtime_abort();
}

void __oxygen_runtime_invalid_cast(
    const oxygen_runtime_type_info *source_type,
    const oxygen_runtime_type_info *target_type)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen cannot cast %s to %s\n",
        source_type == NULL || source_type->name == NULL ? "unknown" : source_type->name,
        target_type == NULL || target_type->name == NULL ? "unknown" : target_type->name);
    fflush(stderr);
    oxygen_runtime_abort();
}
