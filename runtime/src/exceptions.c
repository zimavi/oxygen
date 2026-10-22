#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "oxygen_context.h"

#if defined(_WIN32)
#include <windows.h>
#endif

/* The trace stack of the running thread lives in its program's context (oxygen_context.h). Each function pushes a frame when it starts
 * and pops it when it returns. The stack grows as needed, so a deep call chain keeps every frame. An exception leaves its functions
 * without popping them, so the catch that handles it restores the depth that its `try` saved. */
#define OXYGEN_TRACE_INITIAL_CAPACITY 256

void __oxygen_runtime_trace_push(const char *function_name, const char *file_name,
    uint32_t line, uint32_t column)
{
    oxygen_context *context = oxygen_context_current();
    if (context->trace_depth == context->trace_capacity)
    {
        uint64_t capacity = context->trace_capacity == 0
            ? OXYGEN_TRACE_INITIAL_CAPACITY : context->trace_capacity * 2;
        oxygen_runtime_trace_frame *frames = realloc(context->trace_frames, sizeof(*frames) * capacity);
        if (frames == NULL)
        {
            fprintf(oxygen_runtime_error_stream(), "Oxygen ran out of memory for the stack trace\n");
            fflush(stderr);
            oxygen_runtime_abort();
        }
        context->trace_frames = frames;
        context->trace_capacity = capacity;
    }
    context->trace_frames[context->trace_depth++] = (oxygen_runtime_trace_frame){function_name, file_name, line, column};
}

void __oxygen_runtime_trace_pop(void)
{
    oxygen_context *context = oxygen_context_current();
    if (context->trace_depth != 0)
        --context->trace_depth;
}

uint64_t __oxygen_runtime_trace_depth(void)
{
    return oxygen_context_current()->trace_depth;
}

/* Sets the depth back to a depth saved by a `try`. The depth only goes down: frames above it belong to functions that the
 * exception has already left. */
void __oxygen_runtime_trace_restore(uint64_t depth)
{
    oxygen_context *context = oxygen_context_current();
    if (depth < context->trace_depth)
        context->trace_depth = depth;
}

static void oxygen_capture_trace(void *object)
{
    oxygen_context *context = oxygen_context_current();
    oxygen_exception_trace_record *record = calloc(1, sizeof(*record));
    if (record == NULL)
        oxygen_runtime_abort();
    record->object = object;
    record->trace.count = context->trace_depth;
    if (context->trace_depth != 0)
    {
        record->frames = malloc(sizeof(*record->frames) * context->trace_depth);
        if (record->frames == NULL)
            oxygen_runtime_abort();
        memcpy(record->frames, context->trace_frames, sizeof(*record->frames) * context->trace_depth);
        record->trace.frames = record->frames;
    }
    record->next = context->exception_traces;
    context->exception_traces = record;
}

static const oxygen_runtime_trace *oxygen_find_trace(void *object)
{
    for (oxygen_exception_trace_record *record = oxygen_context_current()->exception_traces;
         record != NULL; record = record->next)
        if (record->object == object)
            return &record->trace;
    return NULL;
}

static void oxygen_release_trace(void *object)
{
    oxygen_exception_trace_record **link = &oxygen_context_current()->exception_traces;
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
        fprintf(oxygen_runtime_error_stream(), "Oxygen stack trace frame %llu is out of bounds for %llu frames\n",
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

/* A runtime failure's text is allocated for its exception object, which holds it. The object's release frees the text. */
void __oxygen_runtime_adopt_failure_text(void *object, void *text)
{
    oxygen_context *context = oxygen_context_current();
    oxygen_failure_text_record *record = malloc(sizeof(*record));
    if (record == NULL)
        oxygen_runtime_abort();
    record->object = object;
    record->text = text;
    record->next = context->failure_texts;
    context->failure_texts = record;
}

/* Releases a caught exception once the catch that handled it has finished with it. The backend runs the object's destructor and frees it
 * after this call. The runtime lets go of the object first: the references to it that the native exception handling keeps, its trace, and
 * the text of a runtime failure. */
void __oxygen_runtime_release_caught_exception(void *object)
{
    oxygen_context *context = oxygen_context_current();
    if (context->in_flight_seh_object == object)
        context->in_flight_seh_object = NULL;
    if (context->current_seh_object == object)
    {
        context->current_seh_object = NULL;
        context->current_seh_flags = 0;
    }
    if (context->current_posix_object == object)
        context->current_posix_object = NULL;
    oxygen_release_trace(object);

    oxygen_failure_text_record **link = &context->failure_texts;
    while (*link != NULL)
    {
        if ((*link)->object == object)
        {
            oxygen_failure_text_record *record = *link;
            *link = record->next;
            free(record->text);
            free(record);
        }
        else
        {
            link = &(*link)->next;
        }
    }
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
#endif

#if defined(_WIN32)
/*
 * LLVM's Windows EH lowering supplies an EXCEPTION_POINTERS value to the
 * filter/handler.  Keep only the active record, never an EXCEPTION_POINTERS
 * stack address, so rethrow remains valid after the filter returns.
 */
/* The Oxygen object of the exception being raised. On Windows, a catch funclet receives the exception code from
 * llvm.eh.exceptionpointer, not the exception record, so the object is handed over through this slot. It is set
 * immediately before RaiseException. A finally that throws and catches internally overwrites it, which is a known
 * limitation of this transport. */
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
    oxygen_context_current()->in_flight_seh_object = object;
    ULONG_PTR arguments[1] = {(ULONG_PTR)object};
    RaiseException(
        (DWORD)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE,
        EXCEPTION_NONCONTINUABLE,
        1,
        arguments);
    oxygen_runtime_abort();
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
    oxygen_context_current()->current_posix_object = payload == NULL ? NULL : *payload;
    return oxygen_context_current()->current_posix_object;
#else
    /* The handle is the exception code of the record being dispatched. Any other code (for example a hardware
     * fault caught by the same catch-all) is not an Oxygen exception. */
    if ((uintptr_t)exception_handle != (uintptr_t)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE)
        return NULL;

    oxygen_context *context = oxygen_context_current();
    context->current_seh_object = context->in_flight_seh_object;
    context->current_seh_flags = 0;
    return context->current_seh_object;
#endif
}

/* Ends the native catch. The trace of the caught object is not released here: the handler may still read its stack trace, so the
 * backend releases it when the catch body has finished. */
void __oxygen_runtime_end_catch(void)
{
#if !defined(_WIN32)
    oxygen_context_current()->current_posix_object = NULL;
    __cxa_end_catch();
#else
    oxygen_context_current()->current_seh_object = NULL;
    oxygen_context_current()->current_seh_flags = 0;
#endif
}

void __oxygen_runtime_rethrow(void)
{
#if !defined(_WIN32)
    __cxa_rethrow();
    oxygen_runtime_abort();
#else
    oxygen_context *context = oxygen_context_current();
    if (context->current_seh_object == NULL)
    {
        fprintf(oxygen_runtime_error_stream(), "Unhandled Exception\n");
        fflush(stderr);
        oxygen_runtime_abort();
    }

    context->in_flight_seh_object = context->current_seh_object;
    ULONG_PTR arguments[1] = {(ULONG_PTR)context->current_seh_object};
    RaiseException(
        (DWORD)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE,
        context->current_seh_flags | EXCEPTION_NONCONTINUABLE,
        1,
        arguments);
    oxygen_runtime_abort();
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
    fprintf(oxygen_runtime_error_stream(), "Unhandled %s", type == NULL || type->name == NULL
        ? "Exception" : display_type_name(type->name));
    if (type != NULL && type->message_offset != 0 && object != NULL)
    {
        const oxygen_string *message =
            (const oxygen_string *)((const unsigned char *)object + type->message_offset);
        if (message->data != NULL)
        {
            fputs(": ", oxygen_runtime_error_stream());
            fwrite(message->data, 1, (size_t)message->length, oxygen_runtime_error_stream());
        }
    }
    const oxygen_runtime_trace *trace = oxygen_find_trace(object);
    if (trace != NULL)
        for (uint64_t i = trace->count; i-- > 0;)
            fprintf(oxygen_runtime_error_stream(), "\n  at %s (%s:%u:%u)",
                trace->frames[i].function_name == NULL ? "<unknown>" : trace->frames[i].function_name,
                trace->frames[i].file_name == NULL ? "<unknown>" : trace->frames[i].file_name,
                trace->frames[i].line, trace->frames[i].column);
    fputc('\n', oxygen_runtime_error_stream());
    fflush(stderr);
    __cxa_end_catch();
    oxygen_release_trace(object);
    oxygen_runtime_abort();
#else
    void *object = __oxygen_runtime_exception_object(exception_handle);
    const oxygen_runtime_type_info *type =
        object == NULL ? NULL : *(const oxygen_runtime_type_info *const *)object;
    fprintf(oxygen_runtime_error_stream(), "Unhandled %s",
        type == NULL || type->name == NULL ? "Exception" : display_type_name(type->name));
    if (type != NULL && type->message_offset != 0 && object != NULL)
    {
        const oxygen_string *message =
            (const oxygen_string *)((const unsigned char *)object + type->message_offset);
        if (message->data != NULL)
        {
            fputs(": ", oxygen_runtime_error_stream());
            fwrite(message->data, 1, (size_t)message->length, oxygen_runtime_error_stream());
        }
    }
    const oxygen_runtime_trace *trace = oxygen_find_trace(object);
    if (trace != NULL)
        for (uint64_t i = trace->count; i-- > 0;)
            fprintf(oxygen_runtime_error_stream(), "\n  at %s (%s:%u:%u)",
                trace->frames[i].function_name == NULL ? "<unknown>" : trace->frames[i].function_name,
                trace->frames[i].file_name == NULL ? "<unknown>" : trace->frames[i].file_name,
                trace->frames[i].line, trace->frames[i].column);
    fputc('\n', oxygen_runtime_error_stream());
    fflush(stderr);
    oxygen_release_trace(object);
    oxygen_runtime_abort();
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
