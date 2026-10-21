#include <stdio.h>
#include <stdlib.h>
#include "oxygen_context.h"

/* The context of the thread that runs a program. Thread-local storage is what makes the runtime serve several programs at once: each
 * thread has its own, and nothing is shared between them. */
static _Thread_local oxygen_context *oxygen_thread_context;

oxygen_context *oxygen_context_current(void)
{
    if (oxygen_thread_context == NULL)
    {
        oxygen_thread_context = calloc(1, sizeof(*oxygen_thread_context));
        if (oxygen_thread_context == NULL)
        {
            fprintf(oxygen_runtime_error_stream(), "Oxygen ran out of memory for the program state\n");
            fflush(stderr);
            oxygen_runtime_abort();
        }
    }
    return oxygen_thread_context;
}

/* Frees the calling thread's context at exit. A later call on the same thread creates a new one, so the release is safe to repeat. */
static void oxygen_context_release(void)
{
    oxygen_context *context = oxygen_thread_context;
    if (context == NULL)
        return;

    oxygen_exception_trace_record *record = context->exception_traces;
    while (record != NULL)
    {
        oxygen_exception_trace_record *next = record->next;
        free(record->frames);
        free(record);
        record = next;
    }
    free(context->trace_frames);
    free(context);
    oxygen_thread_context = NULL;
}

void oxygen_context_begin(void)
{
    if (oxygen_thread_context != NULL)
        return;

    oxygen_context_current();
    if (atexit(oxygen_context_release) != 0)
    {
        fprintf(stderr, "Oxygen could not register its exit handler\n");
        fflush(stderr);
        oxygen_runtime_abort();
    }
}
