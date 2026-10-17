#include <stdio.h>
#include "oxygen_runtime.h"

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
