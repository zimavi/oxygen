#include <stdio.h>
#include <stdlib.h>
#include "oxygen_runtime.h"

// Running out of memory is fatal. The runtime reports it and stops, so no Oxygen code receives NULL.
static void oxygen_out_of_memory(void)
{
    fprintf(stderr, "Oxygen out of memory\n");
    oxygen_runtime_abort();
}

uint64_t oxygen_array_size(uint64_t count, uint64_t element_size)
{
    if (element_size != 0 && count > UINT64_MAX / element_size)
    {
        fprintf(stderr, "Oxygen array of %llu elements of %llu bytes is too large\n",
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
    return result;
}

/* A zero-size request still gets its own non-null pointer, on every platform. Out-of-memory stops the program, so it never returns null. */
void *oxygen_alloc(uint64_t size)
{
    void *result = malloc(size == 0 ? 1 : (size_t)size);
    if (result == NULL)
        oxygen_out_of_memory();
    return result;
}

void oxygen_free(void *pointer)
{
    free(pointer);
}

void *oxygen_calloc(uint64_t count, uint64_t element_size)
{
    uint64_t size = oxygen_array_size(count, element_size);
    void *result = calloc(1, (size_t)(size == 0 ? 1 : size));
    if (result == NULL && size != 0)
        oxygen_out_of_memory();
    return result;
}
