#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "oxygen_runtime.h"

void __oxygen_runtime_initialize(void)
{
}

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

void *oxygen_alloc(uint64_t size)
{
    return malloc((size_t)size);
}

void oxygen_free(void *pointer)
{
    free(pointer);
}

static void oxygen_write(FILE *stream, const oxygen_string *value)
{
    if (value == NULL || value->data == NULL || value->length == 0)
        return;

    fwrite(value->data, 1, (size_t)value->length, stream);
    fflush(stream);
}

static void oxygen_print_signed(long long value, int newline)
{
    fprintf(stdout, "%lld%s", value, newline ? "\n" : "");
    fflush(stdout);
}

static void oxygen_print_unsigned(unsigned long long value, int newline)
{
    fprintf(stdout, "%llu%s", value, newline ? "\n" : "");
    fflush(stdout);
}

static void oxygen_print_float(double value, int newline)
{
    fprintf(stdout, "%.17g%s", value, newline ? "\n" : "");
    fflush(stdout);
}

static void oxygen_print_boolean(uint8_t value, int newline)
{
    fprintf(stdout, "%s%s", value ? "true" : "false", newline ? "\n" : "");
    fflush(stdout);
}

void oxygen_write_stdout(const oxygen_string *value)
{
    oxygen_write(stdout, value);
}

void oxygen_write_stderr(const oxygen_string *value)
{
    oxygen_write(stderr, value);
}

void oxygen_print_i8(int8_t value) { oxygen_print_signed(value, 0); }
void oxygen_print_i16(int16_t value) { oxygen_print_signed(value, 0); }
void oxygen_print_i32(int32_t value) { oxygen_print_signed(value, 0); }
void oxygen_print_i64(int64_t value) { oxygen_print_signed(value, 0); }
void oxygen_print_u8(uint8_t value) { oxygen_print_unsigned(value, 0); }
void oxygen_print_u16(uint16_t value) { oxygen_print_unsigned(value, 0); }
void oxygen_print_u32(uint32_t value) { oxygen_print_unsigned(value, 0); }
void oxygen_print_u64(uint64_t value) { oxygen_print_unsigned(value, 0); }
void oxygen_print_f32(float value) { oxygen_print_float(value, 0); }
void oxygen_print_f64(double value) { oxygen_print_float(value, 0); }
void oxygen_print_bool(uint8_t value) { oxygen_print_boolean(value, 0); }

void oxygen_println_i8(int8_t value) { oxygen_print_signed(value, 1); }
void oxygen_println_i16(int16_t value) { oxygen_print_signed(value, 1); }
void oxygen_println_i32(int32_t value) { oxygen_print_signed(value, 1); }
void oxygen_println_i64(int64_t value) { oxygen_print_signed(value, 1); }
void oxygen_println_u8(uint8_t value) { oxygen_print_unsigned(value, 1); }
void oxygen_println_u16(uint16_t value) { oxygen_print_unsigned(value, 1); }
void oxygen_println_u32(uint32_t value) { oxygen_print_unsigned(value, 1); }
void oxygen_println_u64(uint64_t value) { oxygen_print_unsigned(value, 1); }
void oxygen_println_f32(float value) { oxygen_print_float(value, 1); }
void oxygen_println_f64(double value) { oxygen_print_float(value, 1); }
void oxygen_println_bool(uint8_t value) { oxygen_print_boolean(value, 1); }

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