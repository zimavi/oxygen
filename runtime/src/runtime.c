#include "oxygen_runtime.h"

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#include <stdio.h>
#include <windows.h>
#endif

void __oxygen_runtime_initialize(void)
{
    oxygen_io_initialize();
#if defined(_WIN32)
    /* Oxygen strings are bytes. They are written unchanged: no newline becomes a carriage return and a line feed. */
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
    /* The console shows UTF-8 text only when its output code page is UTF-8. */
    SetConsoleOutputCP(CP_UTF8);
#endif
}
