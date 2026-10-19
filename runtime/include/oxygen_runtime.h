#ifndef OXYGEN_RUNTIME_H
#define OXYGEN_RUNTIME_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define OXYGEN_RUNTIME_EXCEPTION_ABI_VERSION 1u

/* Private exception code used by the Windows SEH bridge.  The payload is the
 * Oxygen object pointer passed to __oxygen_runtime_throw_object. */
#if defined(_WIN32)
#define OXYGEN_RUNTIME_SEH_EXCEPTION_CODE ((uint32_t)0xE0424F58u)
#endif

#if defined(_WIN32) && defined(OXYGEN_RUNTIME_BUILD)
#define OXYGEN_RUNTIME_API __declspec(dllexport)
#else
#define OXYGEN_RUNTIME_API
#endif

typedef struct oxygen_string
{
    const char *data;
    uint64_t length;
} oxygen_string;

typedef struct oxygen_runtime_trace_frame
{
    const char *function_name;
    const char *file_name;
    uint32_t line;
    uint32_t column;
} oxygen_runtime_trace_frame;

typedef struct oxygen_runtime_trace
{
    uint64_t count;
    const oxygen_runtime_trace_frame *frames;
} oxygen_runtime_trace;

typedef enum oxygen_runtime_type_kind
{
    OXYGEN_RUNTIME_TYPE_UNKNOWN = 0,
    OXYGEN_RUNTIME_TYPE_CLASS = 1,
    OXYGEN_RUNTIME_TYPE_INTERFACE = 2
} oxygen_runtime_type_kind;

typedef enum oxygen_runtime_type_flags
{
    OXYGEN_RUNTIME_TYPE_FLAGS_NONE = 0
} oxygen_runtime_type_flags;

typedef struct oxygen_runtime_type_info
{
    const char *name;
    uint32_t kind;
    uint32_t flags;
    const struct oxygen_runtime_type_info *parent;
    uint32_t message_offset;
} oxygen_runtime_type_info;

void __oxygen_runtime_initialize(void);
void __oxygen_runtime_array_bounds_failure(uint64_t index, uint64_t length);
void __oxygen_runtime_enum_cast_failure(uint64_t value);
/* kind: 1 = integer division by zero, 2 = signed division overflow (INT_MIN / -1), 3 = shift count out of range.
   operand is the shift count for kind 3 and 0 otherwise. */
void __oxygen_runtime_arithmetic_failure(int32_t kind, uint64_t operand);
uint8_t __oxygen_runtime_is_instance(
    const void *object,
    const oxygen_runtime_type_info *expected_type);
void __oxygen_runtime_register_type(
    oxygen_runtime_type_info *actual_type,
    const oxygen_runtime_type_info *parent_type);
void __oxygen_runtime_invalid_cast(
    const oxygen_runtime_type_info *source_type,
    const oxygen_runtime_type_info *target_type);
OXYGEN_RUNTIME_API void __oxygen_runtime_trace_push(
    const char *function_name, const char *file_name, uint32_t line, uint32_t column);
OXYGEN_RUNTIME_API void __oxygen_runtime_trace_pop(void);
/* The returned view remains valid until the matching catch is ended or the
 * unhandled-exception reporter returns. */
OXYGEN_RUNTIME_API const oxygen_runtime_trace *__oxygen_runtime_exception_trace(void *object);
/* Returns the captured frame count, or zero when no trace is associated with
 * the object.  The returned count is safe to use after the trace view itself
 * is released and does not expose the runtime-owned frame storage. */
OXYGEN_RUNTIME_API uint64_t __oxygen_runtime_exception_trace_count(void *object);
OXYGEN_RUNTIME_API void *__oxygen_runtime_exception_trace_frame(
    void *object, uint64_t index);
OXYGEN_RUNTIME_API const char *__oxygen_runtime_trace_frame_function_name(
    void *frame);
OXYGEN_RUNTIME_API const char *__oxygen_runtime_trace_frame_file_name(
    void *frame);
OXYGEN_RUNTIME_API uint32_t __oxygen_runtime_trace_frame_line(void *frame);
OXYGEN_RUNTIME_API uint32_t __oxygen_runtime_trace_frame_column(void *frame);
/* Native exception bridge used by LLVM landing pads or Windows funclets. */
OXYGEN_RUNTIME_API void __oxygen_runtime_throw_object(void *object);
OXYGEN_RUNTIME_API void *__oxygen_runtime_exception_object(void *exception_handle);
OXYGEN_RUNTIME_API void __oxygen_runtime_end_catch(void);
OXYGEN_RUNTIME_API void __oxygen_runtime_rethrow(void);
OXYGEN_RUNTIME_API void __oxygen_runtime_unhandled_object(void *exception_handle);
#if defined(_WIN32)
OXYGEN_RUNTIME_API uint8_t __oxygen_runtime_exception_matches(
    void *exception_handle,
    const oxygen_runtime_type_info *expected_type);
#endif

void *oxygen_alloc(uint64_t size);
void oxygen_free(void *pointer);

void oxygen_write_stdout(const oxygen_string *value);
void oxygen_write_stderr(const oxygen_string *value);

const char *oxygen_string_to_c_string(oxygen_string value);
void oxygen_c_string_to_string(const char *value, oxygen_string *result);

#ifdef __cplusplus
}
#endif

#endif
