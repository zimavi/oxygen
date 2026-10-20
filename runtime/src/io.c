#include <stdio.h>
#include "oxygen_runtime.h"

static void oxygen_write(FILE *stream, const oxygen_string *value)
{
    if (value == NULL || value->data == NULL || value->length == 0)
        return;

    fwrite(value->data, 1, (size_t)value->length, stream);
    fflush(stream);
}

void oxygen_write_stdout(const oxygen_string *value)
{
    oxygen_write(stdout, value);
}

void oxygen_write_stderr(const oxygen_string *value)
{
    oxygen_write(stderr, value);
}

// Writes one byte as is. A byte of 128 or more is not a complete UTF-8 character on its own.
void oxygen_write_stdout_byte(uint8_t value)
{
    fwrite(&value, 1, 1, stdout);
    fflush(stdout);
}
