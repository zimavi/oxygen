#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "oxygen_runtime.h"

#if defined(_WIN32)
#include <windows.h>
#endif

/* The trace stack of the running thread. Each function pushes a frame when it starts and pops it when it returns. The stack grows
 * as needed, so a deep call chain keeps every frame. An exception leaves its functions without popping them, so the catch that
 * handles it restores the depth that its `try` saved. */
#define OXYGEN_TRACE_INITIAL_CAPACITY 256
static _Thread_local oxygen_runtime_trace_frame *oxygen_trace_frames;
static _Thread_local uint64_t oxygen_trace_depth;
static _Thread_local uint64_t oxygen_trace_capacity;

typedef struct oxygen_exception_trace_record
{
    void *object;
    oxygen_runtime_trace trace;
    oxygen_runtime_trace_frame *frames;
    struct oxygen_exception_trace_record *next;
} oxygen_exception_trace_record;

static _Thread_local oxygen_exception_trace_record *oxygen_exception_traces;

void __oxygen_runtime_trace_push(const char *function_name, const char *file_name,
    uint32_t line, uint32_t column)
{
    if (oxygen_trace_depth == oxygen_trace_capacity)
    {
        uint64_t capacity = oxygen_trace_capacity == 0
            ? OXYGEN_TRACE_INITIAL_CAPACITY : oxygen_trace_capacity * 2;
        oxygen_runtime_trace_frame *frames =
            realloc(oxygen_trace_frames, sizeof(*frames) * capacity);
        if (frames == NULL)
        {
            fprintf(stderr, "Oxygen ran out of memory for the stack trace\n");
            fflush(stderr);
            oxygen_runtime_abort();
        }
        oxygen_trace_frames = frames;
        oxygen_trace_capacity = capacity;
    }
    oxygen_trace_frames[oxygen_trace_depth++] =
        (oxygen_runtime_trace_frame){function_name, file_name, line, column};
}

void __oxygen_runtime_trace_pop(void)
{
    if (oxygen_trace_depth != 0)
        --oxygen_trace_depth;
}

uint64_t __oxygen_runtime_trace_depth(void)
{
    return oxygen_trace_depth;
}

/* Sets the depth back to a depth saved by a `try`. The depth only goes down: frames above it belong to functions that the
 * exception has already left. */
void __oxygen_runtime_trace_restore(uint64_t depth)
{
    if (depth < oxygen_trace_depth)
        oxygen_trace_depth = depth;
}

static void oxygen_capture_trace(void *object)
{
    oxygen_exception_trace_record *record = calloc(1, sizeof(*record));
    if (record == NULL)
        oxygen_runtime_abort();
    record->object = object;
    record->trace.count = oxygen_trace_depth;
    if (oxygen_trace_depth != 0)
    {
        record->frames = malloc(sizeof(*record->frames) * oxygen_trace_depth);
        if (record->frames == NULL)
            oxygen_runtime_abort();
        memcpy(record->frames, oxygen_trace_frames,
            sizeof(*record->frames) * oxygen_trace_depth);
        record->trace.frames = record->frames;
    }
    record->next = oxygen_exception_traces;
    oxygen_exception_traces = record;
}

static const oxygen_runtime_trace *oxygen_find_trace(void *object)
{
    for (oxygen_exception_trace_record *record = oxygen_exception_traces;
         record != NULL; record = record->next)
        if (record->object == object)
            return &record->trace;
    return NULL;
}

static void oxygen_release_trace(void *object)
{
    oxygen_exception_trace_record **link = &oxygen_exception_traces;
    while (*link != NULL)
    {
        if ((*link)->object == object)
        {
            oxygen_exception_trace_record *record = *link;
            *link = record->next;
            free(record->frames);
            free(record);
            return;
        }
        link = &(*link)->next;
    }
}

const oxygen_runtime_trace *__oxygen_runtime_exception_trace(void *object)
{
    return oxygen_find_trace(object);
}

uint64_t __oxygen_runtime_exception_trace_count(void *object)
{
    const oxygen_runtime_trace *trace = oxygen_find_trace(object);
    return trace == NULL ? 0 : trace->count;
}

void *__oxygen_runtime_exception_trace_frame(void *object, uint64_t index)
{
    const oxygen_runtime_trace *trace = oxygen_find_trace(object);
    uint64_t count = trace == NULL ? 0 : trace->count;
    if (index >= count)
    {
        fprintf(stderr, "Oxygen stack trace frame %llu is out of bounds for %llu frames\n",
            (unsigned long long)index, (unsigned long long)count);
        fflush(stderr);
        oxygen_runtime_abort();
    }
    return (void *)&trace->frames[index];
}

/* Releases the trace of an exception once the catch that handled it has finished with it. */
void __oxygen_runtime_release_exception_trace(void *object)
{
    oxygen_release_trace(object);
}

const char *__oxygen_runtime_trace_frame_function_name(void *frame)
{
    const oxygen_runtime_trace_frame *value =
        (const oxygen_runtime_trace_frame *)frame;
    return value == NULL ? NULL : value->function_name;
}

const char *__oxygen_runtime_trace_frame_file_name(void *frame)
{
    const oxygen_runtime_trace_frame *value =
        (const oxygen_runtime_trace_frame *)frame;
    return value == NULL ? NULL : value->file_name;
}

uint32_t __oxygen_runtime_trace_frame_line(void *frame)
{
    const oxygen_runtime_trace_frame *value =
        (const oxygen_runtime_trace_frame *)frame;
    return value == NULL ? 0 : value->line;
}

uint32_t __oxygen_runtime_trace_frame_column(void *frame)
{
    const oxygen_runtime_trace_frame *value =
        (const oxygen_runtime_trace_frame *)frame;
    return value == NULL ? 0 : value->column;
}

#if !defined(_WIN32)
extern void *__cxa_allocate_exception(size_t);
extern void __cxa_throw(void *, const void *, void (*)(void *));
extern void *__cxa_begin_catch(void *);
extern void __cxa_end_catch(void);
extern void __cxa_rethrow(void);
extern const void *_ZTIv;
static _Thread_local void *oxygen_current_posix_object;
#endif

#if defined(_WIN32)
/*
 * LLVM's Windows EH lowering supplies an EXCEPTION_POINTERS value to the
 * filter/handler.  Keep only the active record, never an EXCEPTION_POINTERS
 * stack address, so rethrow remains valid after the filter returns.
 */
static _Thread_local void *oxygen_current_seh_object;
static _Thread_local DWORD oxygen_current_seh_flags;
/* The Oxygen object of the exception being raised. On Windows, a catch funclet receives the exception code from
 * llvm.eh.exceptionpointer, not the exception record, so the object is handed over through this slot. It is set
 * immediately before RaiseException. A finally that throws and catches internally overwrites it, which is a known
 * limitation of this transport. */
static _Thread_local void *oxygen_in_flight_seh_object;
#endif

/* Raises an exception object that is already being handled, so it continues to unwind. The object and its trace are the ones that
 * were captured at the original throw; the trace is not captured again. */
void __oxygen_runtime_continue_throw(void *object)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_allocate_exception(sizeof(void *));
    if (payload == NULL)
        oxygen_runtime_abort();
    *payload = object;
    __cxa_throw(payload, &_ZTIv, NULL);
#else
    oxygen_in_flight_seh_object = object;
    ULONG_PTR arguments[1] = {(ULONG_PTR)object};
    RaiseException(
        (DWORD)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE,
        EXCEPTION_NONCONTINUABLE,
        1,
        arguments);
    abort();
#endif
}

/* Throws a new exception object: its trace is captured here, at the throw site. */
void __oxygen_runtime_throw_object(void *object)
{
    oxygen_capture_trace(object);
    __oxygen_runtime_continue_throw(object);
}

void *__oxygen_runtime_exception_object(void *exception_handle)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_begin_catch(exception_handle);
    oxygen_current_posix_object = payload == NULL ? NULL : *payload;
    return oxygen_current_posix_object;
#else
    /* The handle is the exception code of the record being dispatched. Any other code (for example a hardware
     * fault caught by the same catch-all) is not an Oxygen exception. */
    if ((uintptr_t)exception_handle != (uintptr_t)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE)
        return NULL;

    oxygen_current_seh_object = oxygen_in_flight_seh_object;
    oxygen_current_seh_flags = 0;
    return oxygen_current_seh_object;
#endif
}

/* Ends the native catch. The trace of the caught object is not released here: the handler may still read its stack trace, so the
 * backend releases it when the catch body has finished. */
void __oxygen_runtime_end_catch(void)
{
#if !defined(_WIN32)
    oxygen_current_posix_object = NULL;
    __cxa_end_catch();
#else
    oxygen_current_seh_object = NULL;
    oxygen_current_seh_flags = 0;
#endif
}

void __oxygen_runtime_rethrow(void)
{
#if !defined(_WIN32)
    __cxa_rethrow();
    abort();
#else
    if (oxygen_current_seh_object == NULL)
    {
        fprintf(stderr, "Unhandled Exception\n");
        fflush(stderr);
        oxygen_runtime_abort();
    }

    oxygen_in_flight_seh_object = oxygen_current_seh_object;
    ULONG_PTR arguments[1] = {(ULONG_PTR)oxygen_current_seh_object};
    RaiseException(
        (DWORD)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE,
        oxygen_current_seh_flags | EXCEPTION_NONCONTINUABLE,
        1,
        arguments);
    abort();
#endif
}

/* The simple name of a type, without its module: `std.exception.Exception` is shown as `Exception`. A specialized name keeps
   its type arguments, which follow `__ox_`. */
static const char *display_type_name(const char *name)
{
    const char *end = strstr(name, "__ox_");
    const char *stop = end == NULL ? name + strlen(name) : end;
    const char *dot = NULL;
    for (const char *p = name; p < stop; p++)
        if (*p == '.')
            dot = p;
    return dot == NULL ? name : dot + 1;
}

void __oxygen_runtime_unhandled_object(void *exception_handle)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_begin_catch(exception_handle);
    void *object = payload == NULL ? NULL : *payload;
    const oxygen_runtime_type_info *type =
        object == NULL ? NULL : *(const oxygen_runtime_type_info *const *)object;
    fprintf(stderr, "Unhandled %s", type == NULL || type->name == NULL
        ? "Exception" : display_type_name(type->name));
    if (type != NULL && type->message_offset != 0 && object != NULL)
    {
        const oxygen_string *message =
            (const oxygen_string *)((const unsigned char *)object + type->message_offset);
        if (message->data != NULL)
        {
            fputs(": ", stderr);
            fwrite(message->data, 1, (size_t)message->length, stderr);
        }
    }
    const oxygen_runtime_trace *trace = oxygen_find_trace(object);
    if (trace != NULL)
        for (uint64_t i = trace->count; i-- > 0;)
            fprintf(stderr, "\n  at %s (%s:%u:%u)",
                trace->frames[i].function_name == NULL ? "<unknown>" : trace->frames[i].function_name,
                trace->frames[i].file_name == NULL ? "<unknown>" : trace->frames[i].file_name,
                trace->frames[i].line, trace->frames[i].column);
    fputc('\n', stderr);
    fflush(stderr);
    __cxa_end_catch();
    oxygen_release_trace(object);
    abort();
#else
    void *object = __oxygen_runtime_exception_object(exception_handle);
    const oxygen_runtime_type_info *type =
        object == NULL ? NULL : *(const oxygen_runtime_type_info *const *)object;
    fprintf(stderr, "Unhandled %s",
        type == NULL || type->name == NULL ? "Exception" : display_type_name(type->name));
    if (type != NULL && type->message_offset != 0 && object != NULL)
    {
        const oxygen_string *message =
            (const oxygen_string *)((const unsigned char *)object + type->message_offset);
        if (message->data != NULL)
        {
            fputs(": ", stderr);
            fwrite(message->data, 1, (size_t)message->length, stderr);
        }
    }
    const oxygen_runtime_trace *trace = oxygen_find_trace(object);
    if (trace != NULL)
        for (uint64_t i = trace->count; i-- > 0;)
            fprintf(stderr, "\n  at %s (%s:%u:%u)",
                trace->frames[i].function_name == NULL ? "<unknown>" : trace->frames[i].function_name,
                trace->frames[i].file_name == NULL ? "<unknown>" : trace->frames[i].file_name,
                trace->frames[i].line, trace->frames[i].column);
    fputc('\n', stderr);
    fflush(stderr);
    oxygen_release_trace(object);
    abort();
#endif
}

#if defined(_WIN32)
uint8_t __oxygen_runtime_exception_matches(
    void *exception_handle,
    const oxygen_runtime_type_info *expected_type)
{
    void *object = __oxygen_runtime_exception_object(exception_handle);
    return object == NULL || expected_type == NULL
        ? 0
        : __oxygen_runtime_is_instance(object, expected_type);
}
#endif
