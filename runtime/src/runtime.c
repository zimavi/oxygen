#include "oxygen_context.h"

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#include <stdio.h>
#include <windows.h>

/* A hardware fault (an access violation, a divide error, a stack overflow) is a runtime failure, not an Oxygen exception. The handler
 * runs before the SEH search, so no catch clause and no finally block sees the fault, whether or not the faulting instruction is inside
 * a call. Oxygen's own exception code and every other code without the NTSTATUS error severity (0xC) are left to the normal search. */
static LONG CALLBACK oxygen_hardware_fault_handler(EXCEPTION_POINTERS *exception)
{
    DWORD code = exception->ExceptionRecord->ExceptionCode;
    if ((code & 0xF0000000u) != 0xC0000000u)
        return EXCEPTION_CONTINUE_SEARCH;

    fprintf(oxygen_runtime_error_stream(), "Oxygen stopped after a hardware fault: exception 0x%08lX at address %p\n",
        (unsigned long)code, exception->ExceptionRecord->ExceptionAddress);
    oxygen_runtime_abort();
}
#endif

/* A program starts here, on the thread that runs it. The runtime is a shared library that can serve several programs, so the start keeps
 * no state of its own: it creates the program's context, and the exit handler, the console settings and the fault handler are each
 * program's own registrations. Their effects are idempotent, and the first fault handler to run stops the process, so a second start
 * does not change what a fault does. */
OXYGEN_RUNTIME_API void __oxygen_runtime_initialize(void)
{
    oxygen_context_begin();
    oxygen_io_initialize();
#if defined(_WIN32)
    /* Oxygen strings are bytes. They are written unchanged: no newline becomes a carriage return and a line feed. */
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
    /* The console shows UTF-8 text only when its output code page is UTF-8. */
    SetConsoleOutputCP(CP_UTF8);
    /* The first handler is called before the others, and before any frame is unwound. */
    if (AddVectoredExceptionHandler(1, oxygen_hardware_fault_handler) == NULL)
    {
        fprintf(oxygen_runtime_error_stream(), "Oxygen could not install its hardware fault handler\n");
        oxygen_runtime_abort();
    }
#endif
}
