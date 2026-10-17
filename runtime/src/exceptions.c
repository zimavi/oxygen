#include <stdio.h>
#include <stdlib.h>
#include "oxygen_runtime.h"

#if !defined(_WIN32)
extern void *__cxa_allocate_exception(size_t);
extern void __cxa_throw(void *, const void *, void (*)(void *));
extern void *__cxa_begin_catch(void *);
extern void __cxa_end_catch(void);
extern void __cxa_rethrow(void);
extern const void *_ZTIv;
#endif

static _Thread_local oxygen_runtime_exception_handler *oxygen_current_handler;

void __oxygen_runtime_push_exception_handler(
    oxygen_runtime_exception_handler *handler)
{
    if (handler == NULL)
        return;

    handler->previous = oxygen_current_handler;
    oxygen_current_handler = handler;
}

void __oxygen_runtime_pop_exception_handler(
    oxygen_runtime_exception_handler *handler)
{
    if (handler == NULL || oxygen_current_handler != handler)
        return;

    oxygen_current_handler = handler->previous;
    handler->previous = NULL;
}

void __oxygen_runtime_raise_exception(
    const oxygen_runtime_exception *exception)
{
    if (oxygen_current_handler != NULL &&
        oxygen_current_handler->callback != NULL)
    {
        oxygen_current_handler->callback(exception);
        return;
    }

    __oxygen_runtime_unhandled_exception(exception);
}

void __oxygen_runtime_unhandled_exception(
    const oxygen_runtime_exception *exception)
{
    const char *type_name = "Exception";
    const char *message = "";

    if (exception != NULL)
    {
        if (exception->type_name != NULL)
            type_name = exception->type_name;
        if (exception->message != NULL)
            message = exception->message;
    }

    fprintf(stderr, "Unhandled %s: %s\n", type_name, message);
    __oxygen_runtime_print_stack_trace(
        exception == NULL ? NULL : &exception->stack_trace);
    fflush(stderr);
    /*
     * abort is intentional: an unhandled exception is a fatal runtime
     * invariant violation and must never return to generated code.  The
     * diagnostic is flushed before terminating so it is not lost when stderr
     * is redirected.
     */
    abort();
}

uint32_t __oxygen_runtime_exception_abi_version(void)
{
    return OXYGEN_RUNTIME_EXCEPTION_ABI_VERSION;
}

void __oxygen_runtime_exception_init(
    oxygen_runtime_exception *exception,
    const char *type_name,
    const char *message)
{
    if (exception == NULL)
        return;

    exception->type_name = type_name;
    exception->message = message;
    exception->stack_trace.frames = NULL;
    exception->stack_trace.count = 0;
}

void __oxygen_runtime_exception_set_stack_trace(
    oxygen_runtime_exception *exception,
    const oxygen_runtime_stack_trace_frame *frames,
    uint64_t count)
{
    if (exception == NULL)
        return;

    exception->stack_trace.frames = frames;
    exception->stack_trace.count = frames == NULL ? 0 : count;
}

const char *__oxygen_runtime_exception_type(
    const oxygen_runtime_exception *exception)
{
    return exception == NULL || exception->type_name == NULL
        ? "Exception" : exception->type_name;
}

const char *__oxygen_runtime_exception_message(
    const oxygen_runtime_exception *exception)
{
    return exception == NULL || exception->message == NULL
        ? "" : exception->message;
}

const oxygen_runtime_stack_trace *__oxygen_runtime_exception_stack_trace(
    const oxygen_runtime_exception *exception)
{
    return exception == NULL ? NULL : &exception->stack_trace;
}

void __oxygen_runtime_throw_object(void *object)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_allocate_exception(sizeof(void *));
    if (payload == NULL)
        abort();
    *payload = object;
    __cxa_throw(payload, &_ZTIv, NULL);
#else
    (void)object;
    __oxygen_runtime_unhandled_exception(NULL);
#endif
}

void *__oxygen_runtime_exception_object(void *exception_handle)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_begin_catch(exception_handle);
    return payload == NULL ? NULL : *payload;
#else
    (void)exception_handle;
    return NULL;
#endif
}

void __oxygen_runtime_end_catch(void)
{
#if !defined(_WIN32)
    __cxa_end_catch();
#endif
}

void __oxygen_runtime_rethrow(void)
{
#if !defined(_WIN32)
    __cxa_rethrow();
    abort();
#else
    __oxygen_runtime_unhandled_exception(NULL);
#endif
}

void __oxygen_runtime_unhandled_object(void *exception_handle)
{
#if !defined(_WIN32)
    void **payload = (void **)__cxa_begin_catch(exception_handle);
    void *object = payload == NULL ? NULL : *payload;
    const oxygen_runtime_type_info *type =
        object == NULL ? NULL : *(const oxygen_runtime_type_info *const *)object;
    fprintf(stderr, "Unhandled %s\n", type == NULL || type->name == NULL
        ? "Exception" : type->name);
    fflush(stderr);
    __cxa_end_catch();
    abort();
#else
    (void)exception_handle;
    __oxygen_runtime_unhandled_exception(NULL);
#endif
}
