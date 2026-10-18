#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "oxygen_runtime.h"

#if defined(_WIN32)
#include <windows.h>
#endif

#define OXYGEN_TRACE_MAX_FRAMES 256
static _Thread_local oxygen_runtime_trace_frame oxygen_trace_frames[OXYGEN_TRACE_MAX_FRAMES];
static _Thread_local uint64_t oxygen_trace_count;

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
    if (oxygen_trace_count == OXYGEN_TRACE_MAX_FRAMES)
        return;
    oxygen_trace_frames[oxygen_trace_count++] =
        (oxygen_runtime_trace_frame){function_name, file_name, line, column};
}

void __oxygen_runtime_trace_pop(void)
{
    if (oxygen_trace_count != 0)
        --oxygen_trace_count;
}

static void oxygen_capture_trace(void *object)
{
    oxygen_exception_trace_record *record = calloc(1, sizeof(*record));
    if (record == NULL)
        abort();
    record->object = object;
    record->trace.count = oxygen_trace_count;
    if (oxygen_trace_count != 0)
    {
        record->frames = malloc(sizeof(*record->frames) * oxygen_trace_count);
        if (record->frames == NULL)
            abort();
        memcpy(record->frames, oxygen_trace_frames,
            sizeof(*record->frames) * oxygen_trace_count);
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
    return trace == NULL || trace->frames == NULL || index >= trace->count
        ? NULL : (void *)&trace->frames[index];
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
#endif

void __oxygen_runtime_throw_object(void *object)
{
    oxygen_capture_trace(object);
#if !defined(_WIN32)
    void **payload = (void **)__cxa_allocate_exception(sizeof(void *));
    if (payload == NULL)
        abort();
    *payload = object;
    __cxa_throw(payload, &_ZTIv, NULL);
#else
    ULONG_PTR arguments[1] = {(ULONG_PTR)object};
    RaiseException(
        (DWORD)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE,
        EXCEPTION_NONCONTINUABLE,
        1,
        arguments);
    abort();
#endif
}

void *__oxygen_runtime_exception_object(void *exception_handle)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_begin_catch(exception_handle);
    oxygen_current_posix_object = payload == NULL ? NULL : *payload;
    return oxygen_current_posix_object;
#else
    EXCEPTION_POINTERS *pointers = (EXCEPTION_POINTERS *)exception_handle;
    if (pointers == NULL || pointers->ExceptionRecord == NULL ||
        pointers->ExceptionRecord->ExceptionCode !=
            (DWORD)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE ||
        pointers->ExceptionRecord->NumberParameters < 1)
        return NULL;

    oxygen_current_seh_object =
        (void *)(ULONG_PTR)pointers->ExceptionRecord->ExceptionInformation[0];
    oxygen_current_seh_flags = pointers->ExceptionRecord->ExceptionFlags;
    return oxygen_current_seh_object;
#endif
}

void __oxygen_runtime_end_catch(void)
{
#if !defined(_WIN32)
    oxygen_release_trace(oxygen_current_posix_object);
    oxygen_current_posix_object = NULL;
    /* The active object is owned by the native EH runtime; release only our
     * trace copy after the handler has finished. */
    __cxa_end_catch();
#else
    oxygen_release_trace(oxygen_current_seh_object);
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
        abort();
    }

    ULONG_PTR arguments[1] = {(ULONG_PTR)oxygen_current_seh_object};
    RaiseException(
        (DWORD)OXYGEN_RUNTIME_SEH_EXCEPTION_CODE,
        oxygen_current_seh_flags | EXCEPTION_NONCONTINUABLE,
        1,
        arguments);
    abort();
#endif
}

void __oxygen_runtime_unhandled_object(void *exception_handle)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_begin_catch(exception_handle);
    void *object = payload == NULL ? NULL : *payload;
    const oxygen_runtime_type_info *type =
        object == NULL ? NULL : *(const oxygen_runtime_type_info *const *)object;
    fprintf(stderr, "Unhandled %s", type == NULL || type->name == NULL
        ? "Exception" : type->name);
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
        type == NULL || type->name == NULL ? "Exception" : type->name);
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
