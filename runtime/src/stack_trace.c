#include <stdio.h>
#include "oxygen_runtime.h"

void __oxygen_runtime_print_stack_trace(
    const oxygen_runtime_stack_trace *stack_trace)
{
    uint64_t index;

    fprintf(stderr, "Stack trace:\n");
    if (stack_trace == NULL || stack_trace->frames == NULL)
        return;

    for (index = 0; index < stack_trace->count; ++index)
    {
        const oxygen_runtime_stack_trace_frame *frame =
            &stack_trace->frames[index];
        const char *function_name =
            frame->function_name == NULL ? "<unknown>" : frame->function_name;

        if (frame->file_name == NULL)
        {
            fprintf(stderr, "  at %s\n", function_name);
        }
        else if (frame->line == 0)
        {
            fprintf(stderr, "  at %s (%s)\n",
                function_name, frame->file_name);
        }
        else
        {
            fprintf(stderr, "  at %s (%s:%u",
                function_name, frame->file_name, frame->line);
            if (frame->column != 0)
                fprintf(stderr, ":%u", frame->column);
            fprintf(stderr, ")\n");
        }
    }
}

uint64_t __oxygen_runtime_stack_trace_count(
    const oxygen_runtime_stack_trace *stack_trace)
{
    return stack_trace == NULL || stack_trace->frames == NULL
        ? 0 : stack_trace->count;
}

const oxygen_runtime_stack_trace_frame *
__oxygen_runtime_stack_trace_frame_at(
    const oxygen_runtime_stack_trace *stack_trace, uint64_t index)
{
    if (stack_trace == NULL || stack_trace->frames == NULL ||
        index >= stack_trace->count)
        return NULL;

    return &stack_trace->frames[index];
}
