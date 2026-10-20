#include <stdio.h>
#include <stdlib.h>
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

/* Standard output is buffered. Whatever is still in the buffer when the program returns from main is written here, before the C library
 * flushes the stream itself, so a failure is reported with the runtime's exit code. */
static void oxygen_flush_stdout_at_exit(void)
{
    if (fflush(stdout) != 0)
        oxygen_write_failure(stdout);
}

void oxygen_io_initialize(void)
{
    if (atexit(oxygen_flush_stdout_at_exit) != 0)
    {
        fprintf(stderr, "Oxygen could not register its exit handler\n");
        fflush(stderr);
        oxygen_runtime_abort();
    }
}

/* Every message to standard error goes through here. Buffered standard output is written first, so the two streams show what the program
 * wrote in the same order. */
FILE *oxygen_runtime_error_stream(void)
{
    if (fflush(stdout) != 0)
        oxygen_write_failure(stdout);
    return stderr;
}

static void oxygen_write(FILE *stream, const oxygen_string *value)
{
    if (value == NULL || value->data == NULL || value->length == 0)
        return;

    if (fwrite(value->data, 1, (size_t)value->length, stream) != (size_t)value->length)
        oxygen_write_failure(stream);
}

void oxygen_write_stdout(const oxygen_string *value)
{
    oxygen_write(stdout, value);
}

void oxygen_write_stderr(const oxygen_string *value)
{
    oxygen_write(oxygen_runtime_error_stream(), value);
    if (fflush(stderr) != 0)
        oxygen_write_failure(stderr);
}

// Writes one byte as is. A byte of 128 or more is not a complete UTF-8 character on its own.
void oxygen_write_stdout_byte(uint8_t value)
{
    if (fwrite(&value, 1, 1, stdout) != 1)
        oxygen_write_failure(stdout);
}
