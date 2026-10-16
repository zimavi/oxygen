#ifndef OXYGEN_RUNTIME_H
#define OXYGEN_RUNTIME_H

#include <stdint.h>

typedef struct oxygen_string
{
    const char *data;
    uint64_t length;
} oxygen_string;

void __oxygen_runtime_initialize(void);

void oxygen_write_stdout(const oxygen_string *value);
void oxygen_write_stderr(const oxygen_string *value);

const char *oxygen_string_to_c_string(oxygen_string value);
oxygen_string oxygen_c_string_to_string(const char *value);

#endif
