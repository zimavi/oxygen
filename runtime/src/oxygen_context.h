#ifndef OXYGEN_CONTEXT_H
#define OXYGEN_CONTEXT_H

#include <stdint.h>
#include "oxygen_runtime.h"

#if defined(_WIN32)
#include <windows.h>
#endif

/* The state of one program. The runtime is a shared library that can serve several programs in one process, so it keeps no state of
 * its own. Each program has a context, bound to the thread that runs it: the program's first call into the runtime creates the context,
 * and it is freed when the process exits. Two programs running on two threads therefore never see each other's trace or exception. */
typedef struct oxygen_exception_trace_record
{
    void *object;
    oxygen_runtime_trace trace;
    oxygen_runtime_trace_frame *frames;
    struct oxygen_exception_trace_record *next;
} oxygen_exception_trace_record;

typedef struct oxygen_context
{
    /* The trace stack of the thread: each function pushes a frame when it starts and pops it when it returns. */
    oxygen_runtime_trace_frame *trace_frames;
    uint64_t trace_depth;
    uint64_t trace_capacity;
    /* The traces of the exceptions that are being handled. */
    oxygen_exception_trace_record *exception_traces;
    /* The exception objects that the native exception handling is carrying. */
    void *current_posix_object;
    void *current_seh_object;
    uint32_t current_seh_flags;
    void *in_flight_seh_object;
} oxygen_context;

/* The context of the calling thread. It is created on first use. */
oxygen_context *oxygen_context_current(void);

/* Called by a program when it starts. It creates the thread's context and arranges for it to be freed when the process exits. */
void oxygen_context_begin(void);

#endif
