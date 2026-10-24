#include <stdio.h>
#include <stdlib.h>
#include <string.h>
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

/* A null pointer was dereferenced in a program built with --no-std, which has no NullReferenceException to throw. It stops the program
 * with exit code 101, as a null class reference does. */
void __oxygen_runtime_null_pointer_failure(void)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen stopped: a null pointer was dereferenced\n");
    fflush(stderr);
    oxygen_runtime_abort();
}

/* The text of a cast that failed. The object is the one that failed the cast, read for its class, or null when there is none. */
static int oxygen_write_invalid_cast(char *text, size_t size, const void *object, const oxygen_runtime_type_info *target)
{
    const char *target_name = target == NULL || target->name == NULL ? "unknown" : target->name;
    if (object == NULL)
        return snprintf(text, size, "Cannot cast a null reference to %s.", target_name);
    const oxygen_runtime_type_info *actual = *(const oxygen_runtime_type_info *const *)object;
    const char *actual_name = actual == NULL || actual->name == NULL ? "unknown" : actual->name;
    return snprintf(text, size, "Cannot cast an object of type %s to %s, because it is not an instance of %s.",
        actual_name, target_name, target_name);
}

/* The text of a failure that a catch can handle is formatted with snprintf and copied into memory from oxygen_alloc. The texts are short,
 * so a value that makes one longer than the buffer is cut at the buffer's end. */
void __oxygen_runtime_format_failure(int32_t kind, uint64_t first, uint64_t second, oxygen_string *message)
{
    char text[256];
    int written;
    switch (kind)
    {
    case OXYGEN_FAILURE_ARRAY_INDEX:
        written = snprintf(text, sizeof text, "Array index %llu is outside the bounds of the array of length %llu.",
            (unsigned long long)first, (unsigned long long)second);
        break;
    case OXYGEN_FAILURE_STRING_INDEX:
        written = snprintf(text, sizeof text, "String index %llu is outside the bounds of the string of length %llu.",
            (unsigned long long)first, (unsigned long long)second);
        break;
    case OXYGEN_FAILURE_DIVIDE_BY_ZERO:
        written = snprintf(text, sizeof text, "Integer division by zero.");
        break;
    case OXYGEN_FAILURE_SIGNED_OVERFLOW:
        written = snprintf(text, sizeof text, "Signed integer division overflowed: the minimum value was divided by -1.");
        break;
    case OXYGEN_FAILURE_SHIFT_OUT_OF_RANGE:
        written = snprintf(text, sizeof text, "Shift count %llu is out of range for the shifted integer type.",
            (unsigned long long)first);
        break;
    case OXYGEN_FAILURE_INVALID_ENUM_VALUE:
        written = snprintf(text, sizeof text, "The value %llu is not a case of the enum.", (unsigned long long)first);
        break;
    case OXYGEN_FAILURE_INVALID_CAST:
        written = oxygen_write_invalid_cast(text, sizeof text, (const void *)(uintptr_t)first,
            (const oxygen_runtime_type_info *)(uintptr_t)second);
        break;
    case OXYGEN_FAILURE_FRAME_INDEX:
        written = snprintf(text, sizeof text, "Stack trace frame %llu does not exist: the trace has %llu frames.",
            (unsigned long long)first, (unsigned long long)second);
        break;
    default:
        written = snprintf(text, sizeof text, "Oxygen failure %d.", (int)kind);
        break;
    }
    size_t length = written < 0 ? 0 : (size_t)written;
    if (length >= sizeof text)
        length = sizeof text - 1;
    char *data = oxygen_alloc((uint64_t)length);
    memcpy(data, text, length);
    message->data = data;
    message->length = (uint64_t)length;
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

void __oxygen_runtime_invalid_cast(const void *object, const oxygen_runtime_type_info *target_type)
{
    char text[256];
    oxygen_write_invalid_cast(text, sizeof text, object, target_type);
    fprintf(oxygen_runtime_error_stream(), "Oxygen: %s\n", text);
    fflush(stderr);
    oxygen_runtime_abort();
}

void __oxygen_runtime_set_failure_message(void *object, int32_t kind, uint64_t first, uint64_t second)
{
    if (object == NULL)
        return;
    oxygen_string text = {NULL, 0};
    __oxygen_runtime_format_failure(kind, first, second, &text);
    const oxygen_runtime_type_info *type = *(const oxygen_runtime_type_info *const *)object;
    *(oxygen_string *)((unsigned char *)object + type->message_offset) = text;
    __oxygen_runtime_adopt_failure_text(object, (void *)text.data);
}
