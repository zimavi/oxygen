#include <stdio.h>
#include <stdlib.h>
#include "oxygen_runtime.h"

// Running out of memory is fatal. The runtime reports it and stops, so no Oxygen code receives NULL.
static void oxygen_out_of_memory(void)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen out of memory\n");
    oxygen_runtime_abort();
}

/* The number of blocks that the program allocated and has not freed. When OXYGEN_LEAK_REPORT is set, the number is printed at exit, so a
 * program's leaks can be checked. The count is kept whether or not the report is on. */
static uint64_t oxygen_live_blocks = 0;
static int oxygen_leak_report_registered = 0;

static void oxygen_report_leaks(void)
{
    fprintf(oxygen_runtime_error_stream(), "Oxygen: %llu allocation(s) were not freed\n", (unsigned long long)oxygen_live_blocks);
}

static int oxygen_leak_report_requested(void)
{
#if defined(_WIN32)
    /* getenv is deprecated on Windows, and _dupenv_s returns a copy that the caller frees. */
    char *value = NULL;
    size_t length = 0;
    int found = _dupenv_s(&value, &length, "OXYGEN_LEAK_REPORT") == 0 && value != NULL;
    free(value);
    return found;
#else
    return getenv("OXYGEN_LEAK_REPORT") != NULL;
#endif
}

void oxygen_memory_initialize(void)
{
    if (oxygen_leak_report_registered || !oxygen_leak_report_requested())
        return;
    oxygen_leak_report_registered = 1;
    atexit(oxygen_report_leaks);
}

uint64_t oxygen_array_size(uint64_t count, uint64_t element_size)
{
    if (element_size != 0 && count > UINT64_MAX / element_size)
    {
        fprintf(oxygen_runtime_error_stream(), "Oxygen array of %llu elements of %llu bytes is too large\n",
            (unsigned long long)count, (unsigned long long)element_size);
        oxygen_runtime_abort();
    }
    return count * element_size;
}

void *oxygen_realloc(void *pointer, uint64_t size)
{
    void *result = realloc(pointer, (size_t)size);
    if (result == NULL && size != 0)
        oxygen_out_of_memory();
    /* A block is new when there was none, and gone when a zero-size request released it. A moved block is still one block. */
    if (pointer == NULL && result != NULL)
        oxygen_live_blocks++;
    else if (pointer != NULL && result == NULL)
        oxygen_live_blocks--;
    return result;
}

/* A zero-size request still gets its own non-null pointer, on every platform. Out-of-memory stops the program, so it never returns null. */
void *oxygen_alloc(uint64_t size)
{
    void *result = malloc(size == 0 ? 1 : (size_t)size);
    if (result == NULL)
        oxygen_out_of_memory();
    oxygen_live_blocks++;
    return result;
}

void oxygen_free(void *pointer)
{
    if (pointer != NULL)
        oxygen_live_blocks--;
    free(pointer);
}

void *oxygen_calloc(uint64_t count, uint64_t element_size)
{
    uint64_t size = oxygen_array_size(count, element_size);
    void *result = calloc(1, (size_t)(size == 0 ? 1 : size));
    if (result == NULL && size != 0)
        oxygen_out_of_memory();
    oxygen_live_blocks++;
    return result;
}
