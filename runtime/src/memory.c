#include <stdlib.h>
#include "oxygen_runtime.h"

void *oxygen_alloc(uint64_t size)
{
    return malloc((size_t)size);
}

void oxygen_free(void *pointer)
{
    free(pointer);
}
