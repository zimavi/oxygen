#ifndef OXYGEN_RUNTIME_H
#define OXYGEN_RUNTIME_H

#include <stdint.h>
#include <stddef.h>

typedef struct oxygen_string
{
    const char *data;
    uint64_t length;
} oxygen_string;

void __oxygen_runtime_initialize(void);
void __oxygen_runtime_array_bounds_failure(uint64_t index, uint64_t length);
void __oxygen_runtime_enum_cast_failure(uint64_t value);
uint8_t __oxygen_runtime_is_instance(const void *object, const char *expected_type);
void __oxygen_runtime_invalid_cast(const char *source_type, const char *target_type);

void *oxygen_alloc(uint64_t size);
void oxygen_free(void *pointer);

void oxygen_write_stdout(const oxygen_string *value);
void oxygen_write_stderr(const oxygen_string *value);

void oxygen_print_i8(int8_t value);
void oxygen_print_i16(int16_t value);
void oxygen_print_i32(int32_t value);
void oxygen_print_i64(int64_t value);
void oxygen_print_u8(uint8_t value);
void oxygen_print_u16(uint16_t value);
void oxygen_print_u32(uint32_t value);
void oxygen_print_u64(uint64_t value);
void oxygen_print_f32(float value);
void oxygen_print_f64(double value);
void oxygen_print_bool(uint8_t value);

void oxygen_println_i8(int8_t value);
void oxygen_println_i16(int16_t value);
void oxygen_println_i32(int32_t value);
void oxygen_println_i64(int64_t value);
void oxygen_println_u8(uint8_t value);
void oxygen_println_u16(uint16_t value);
void oxygen_println_u32(uint32_t value);
void oxygen_println_u64(uint64_t value);
void oxygen_println_f32(float value);
void oxygen_println_f64(double value);
void oxygen_println_bool(uint8_t value);

const char *oxygen_string_to_c_string(oxygen_string value);
oxygen_string oxygen_c_string_to_string(const char *value);

#endif
