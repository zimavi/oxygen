#ifndef OXYGEN_RUNTIME_H
#define OXYGEN_RUNTIME_H

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>

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
    /* Not used by the runtime, and always NULL: a pointer to another type's record cannot be a constant when that record is in a shared
     * library on Windows. The supertype names below give the relations. */
    const struct oxygen_runtime_type_info *parent;
    uint32_t message_offset;
    /* The most derived destructor for this type, or NULL. Releasing an object calls it through its runtime type. */
    void (*destructor)(void *object);
    /* The names of every base class and interface of this type, nearest first, without the type itself. The compiler emits the list.
     * Names are module-qualified and unique, so a type is found by its name as well as by its address. */
    const char *const *supertype_names;
    uint32_t supertype_count;
} oxygen_runtime_type_info;

/* Flags for __oxygen_runtime_initialize. The compiler sets them from the program's mode. The runtime ignores the bits it does not know. */
typedef enum oxygen_runtime_flag
{
    OXYGEN_RUNTIME_EXCEPTIONS_ENABLED = 1u << 0
} oxygen_runtime_flag;

OXYGEN_RUNTIME_API void __oxygen_runtime_initialize(uint64_t flags);
/* Ends the program after a runtime failure with exit code 101. The caller writes the message first. */
OXYGEN_RUNTIME_API _Noreturn void oxygen_runtime_abort(void);
OXYGEN_RUNTIME_API void __oxygen_runtime_array_bounds_failure(uint64_t index, uint64_t length);
OXYGEN_RUNTIME_API void __oxygen_runtime_string_bounds_failure(uint64_t index, uint64_t length);
OXYGEN_RUNTIME_API void __oxygen_runtime_enum_cast_failure(uint64_t value);
/* kind: 1 = integer division by zero, 2 = signed division overflow (INT_MIN / -1), 3 = shift count out of range.
   operand is the shift count for kind 3 and 0 otherwise. */
/* Stops the program, with exit code 101, when a null class reference is used in a program that has no NullReferenceException (see --no-std). */
OXYGEN_RUNTIME_API void __oxygen_runtime_null_reference_failure(void);
/* Stops the program, with exit code 101, when a null pointer is dereferenced in a program that has no NullReferenceException (see --no-std). */
OXYGEN_RUNTIME_API void __oxygen_runtime_null_pointer_failure(void);
OXYGEN_RUNTIME_API void __oxygen_runtime_arithmetic_failure(int32_t kind, uint64_t operand);

/* The failures that a catch can handle, as the compiler numbers them. The compiler's helper of each failure passes its number, and the
 * values that the message shows, to __oxygen_runtime_format_failure. */
#define OXYGEN_FAILURE_ARRAY_INDEX 1         /* first: the index, second: the length of the array */
#define OXYGEN_FAILURE_STRING_INDEX 2        /* first: the index, second: the length of the string */
#define OXYGEN_FAILURE_DIVIDE_BY_ZERO 3      /* no values */
#define OXYGEN_FAILURE_SIGNED_OVERFLOW 4     /* no values */
#define OXYGEN_FAILURE_SHIFT_OUT_OF_RANGE 5  /* first: the shift count */
#define OXYGEN_FAILURE_INVALID_ENUM_VALUE 6  /* first: the value */

/* Writes the message of a failure into memory from oxygen_alloc, and returns it in message. The memory has the lifetime of the exception
 * that holds the message. */
OXYGEN_RUNTIME_API void __oxygen_runtime_format_failure(int32_t kind, uint64_t first, uint64_t second, oxygen_string *message);
OXYGEN_RUNTIME_API uint8_t __oxygen_runtime_is_instance(
    const void *object,
    const oxygen_runtime_type_info *expected_type);
OXYGEN_RUNTIME_API void __oxygen_runtime_invalid_cast(
    const oxygen_runtime_type_info *source_type,
    const oxygen_runtime_type_info *target_type);
OXYGEN_RUNTIME_API void __oxygen_runtime_trace_push(
    const char *function_name, const char *file_name, uint32_t line, uint32_t column);
OXYGEN_RUNTIME_API void __oxygen_runtime_trace_pop(void);
/* The number of frames on the trace stack. A `try` saves it, and the catch that handles an exception restores it. */
OXYGEN_RUNTIME_API uint64_t __oxygen_runtime_trace_depth(void);
OXYGEN_RUNTIME_API void __oxygen_runtime_trace_restore(uint64_t depth);
/* Releases the trace of a caught exception. Called when the catch body has finished. */
OXYGEN_RUNTIME_API void __oxygen_runtime_release_exception_trace(void *object);
/* Holds the text of a runtime failure in its exception object, which frees it when released. */
OXYGEN_RUNTIME_API void __oxygen_runtime_adopt_failure_text(void *object, void *text);
/* Releases a caught exception after its catch: its trace, the runtime's references to it, and the text of a runtime failure. The backend
 * then runs its destructor and frees the object. */
OXYGEN_RUNTIME_API void __oxygen_runtime_release_caught_exception(void *object);
/* The returned view remains valid until the catch body that handles the
 * exception has finished, or the unhandled-exception reporter returns. */
OXYGEN_RUNTIME_API const oxygen_runtime_trace *__oxygen_runtime_exception_trace(void *object);
/* Returns the captured frame count, or zero when no trace is associated with
 * the object.  The count is zero once the catch that handled the exception has
 * ended.  The returned count is safe to use after the trace view itself is
 * released and does not expose the runtime-owned frame storage. */
OXYGEN_RUNTIME_API uint64_t __oxygen_runtime_exception_trace_count(void *object);
/* Aborts the program when index is not less than the frame count. */
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
/* Continues the unwinding of an exception object that a catch clause is handling (`throw;`). Its trace is kept. */
OXYGEN_RUNTIME_API void __oxygen_runtime_continue_throw(void *object);
OXYGEN_RUNTIME_API void *__oxygen_runtime_exception_object(void *exception_handle);
OXYGEN_RUNTIME_API void __oxygen_runtime_end_catch(void);
OXYGEN_RUNTIME_API void __oxygen_runtime_rethrow(void);
OXYGEN_RUNTIME_API void __oxygen_runtime_unhandled_object(void *exception_handle);
/* A throw in a program built without exceptions: the object is reported, and the program ends with exit code 101. */
OXYGEN_RUNTIME_API _Noreturn void __oxygen_runtime_unhandled_instance(void *object);
#if defined(_WIN32)
OXYGEN_RUNTIME_API uint8_t __oxygen_runtime_exception_matches(
    void *exception_handle,
    const oxygen_runtime_type_info *expected_type);
#endif

OXYGEN_RUNTIME_API uint64_t oxygen_array_size(uint64_t count, uint64_t element_size);
OXYGEN_RUNTIME_API void *oxygen_alloc(uint64_t size);
OXYGEN_RUNTIME_API void *oxygen_realloc(void *pointer, uint64_t size);
OXYGEN_RUNTIME_API void *oxygen_calloc(uint64_t count, uint64_t element_size);
OXYGEN_RUNTIME_API void oxygen_free(void *pointer);

OXYGEN_RUNTIME_API void oxygen_io_initialize(void);
/* Starts the leak report when OXYGEN_LEAK_REPORT is set: the allocations that are not freed are printed at exit. */
OXYGEN_RUNTIME_API void oxygen_memory_initialize(void);
OXYGEN_RUNTIME_API FILE *oxygen_runtime_error_stream(void);
OXYGEN_RUNTIME_API void oxygen_write_stdout(const oxygen_string *value);
OXYGEN_RUNTIME_API void oxygen_write_stderr(const oxygen_string *value);
OXYGEN_RUNTIME_API void oxygen_write_stdout_byte(uint8_t value);

/* A new NUL-terminated copy that the caller owns and frees with oxygen_free, or NULL when the string has an embedded NUL. */
OXYGEN_RUNTIME_API const char *oxygen_string_to_c_string(const oxygen_string *value);
OXYGEN_RUNTIME_API void oxygen_c_string_to_string(const char *value, oxygen_string *result);

#ifdef __cplusplus
}
#endif

#endif
