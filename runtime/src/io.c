#include <stdio.h>
#include "oxygen_runtime.h"

/* A failed write to a standard stream is a runtime failure: a closed pipe, a full disk or a broken terminal. It is reported and the
 * program stops with the runtime's exit code, like the other failures. */
static void oxygen_write_failure(FILE *stream)
{
    fprintf(stderr, "Oxygen could not write to %s\n",
        stream == stdout ? "standard output" : "standard error");
    fflush(stderr);
    oxygen_runtime_abort();
}

static void oxygen_write(FILE *stream, const oxygen_string *value)
{
    if (value == NULL || value->data == NULL || value->length == 0)
        return;

    if (fwrite(value->data, 1, (size_t)value->length, stream) != (size_t)value->length)
        oxygen_write_failure(stream);
    if (fflush(stream) != 0)
        oxygen_write_failure(stream);
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
    if (fwrite(&value, 1, 1, stdout) != 1)
        oxygen_write_failure(stdout);
    if (fflush(stdout) != 0)
        oxygen_write_failure(stdout);
}
