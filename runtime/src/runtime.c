#include "oxygen_runtime.h"

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

/* The process setup (the exit handler, the console, the hardware fault handler) belongs to the process, not to one program. A runtime that
 * is loaded as a shared library can start more than one program, so the setup runs once, however many programs start. A second start
 * would otherwise install the fault handler twice, and every fault would be reported twice. */
static volatile long oxygen_process_setup_done;

static int oxygen_claim_process_setup(void)
{
#if defined(_WIN32)
    return InterlockedCompareExchange(&oxygen_process_setup_done, 1, 0) == 0;
#else
    return __sync_bool_compare_and_swap(&oxygen_process_setup_done, 0, 1);
#endif
}

OXYGEN_RUNTIME_API void __oxygen_runtime_initialize(void)
{
    if (!oxygen_claim_process_setup())
        return;
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
