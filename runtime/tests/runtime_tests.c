/* Native tests for runtime functions that Oxygen programs cannot reach directly. The program runs one named case, given on the
 * command line, so a case that ends the process (a runtime failure) can be checked by its exit code. A passing case returns 0. A
 * failing check prints its line and returns 1. An unknown case name returns 2.
 *
 * Build: clang -I runtime/include runtime/tests/runtime_tests.c liboxygenrt.a -o runtime_tests */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdatomic.h>
#if defined(_WIN32)
#include <windows.h>
#else
#include <pthread.h>
#endif
#include "oxygen_runtime.h"

#define CHECK(condition)                                                              \
    do {                                                                              \
        if (!(condition)) {                                                           \
            fprintf(stderr, "check failed at line %d: %s\n", __LINE__, #condition);   \
            return 1;                                                                 \
        }                                                                             \
    } while (0)

static int c_string_from_null(void)
{
    oxygen_string result = {"unused", 6};
    oxygen_c_string_to_string(NULL, &result);
    CHECK(result.data == NULL);
    CHECK(result.length == 0);
    return 0;
}

static int c_string_from_text(void)
{
    static const char text[] = "abc";
    oxygen_string result = {NULL, 0};
    oxygen_c_string_to_string(text, &result);
    CHECK(result.data == text);
    CHECK(result.length == 3);
    return 0;
}

static int string_to_c_string_copies_and_terminates(void)
{
    static const char bytes[] = "abc";
    oxygen_string value = {bytes, 3};
    char *copy = (char *)oxygen_string_to_c_string(&value);
    CHECK(copy != NULL);
    CHECK(copy != bytes);
    CHECK(strcmp(copy, "abc") == 0);
    /* The caller owns the copy and frees it with the runtime's allocator. */
    oxygen_free(copy);
    return 0;
}

static int string_with_embedded_zero_has_no_c_string(void)
{
    static const char bytes[] = "a\0b";
    oxygen_string value = {bytes, 3};
    CHECK(oxygen_string_to_c_string(&value) == NULL);
    return 0;
}

static int array_size_within_range(void)
{
    CHECK(oxygen_array_size(10, 4) == 40);
    CHECK(oxygen_array_size(0, 8) == 0);
    CHECK(oxygen_array_size(5, 0) == 0);
    return 0;
}

static int array_size_overflow_stops_the_program(void)
{
    /* Ends the process with exit code 101 and a message. */
    oxygen_array_size(UINT64_MAX, 2);
    return 1;
}

static int calloc_zeroes_every_byte(void)
{
    unsigned char *bytes = oxygen_calloc(4, 8);
    CHECK(bytes != NULL);
    for (size_t index = 0; index < 32; index++)
        CHECK(bytes[index] == 0);
    oxygen_free(bytes);
    return 0;
}

static int calloc_overflow_stops_the_program(void)
{
    oxygen_calloc(UINT64_MAX, 2);
    return 1;
}

static int zero_size_alloc_is_non_null_and_freeable(void)
{
    void *pointer = oxygen_alloc(0);
    CHECK(pointer != NULL);
    oxygen_free(pointer);
    return 0;
}

static int trace_stack_grows_past_its_initial_capacity(void)
{
    /* The initial capacity is 256 frames, so 300 pushes grow the stack. */
    for (uint64_t index = 0; index < 300; index++)
        __oxygen_runtime_trace_push("frame", "file.oxy", (uint32_t)index, 1);
    CHECK(__oxygen_runtime_trace_depth() == 300);

    /* A catch restores the depth a try saved, discarding the frames the exception left. */
    __oxygen_runtime_trace_restore(10);
    CHECK(__oxygen_runtime_trace_depth() == 10);

    for (uint64_t index = 0; index < 10; index++)
        __oxygen_runtime_trace_pop();
    CHECK(__oxygen_runtime_trace_depth() == 0);
    return 0;
}

static int write_stdout_writes_embedded_zero_bytes(void)
{
    static const char bytes[] = "a\0b";
    oxygen_string value = {bytes, 3};
    oxygen_write_stdout(&value);
    return 0;
}

static int abort_delivers_buffered_stdout(void)
{
    oxygen_write_stdout_byte('x');
    oxygen_runtime_abort();
}

/* Two threads run two programs on one runtime. Each pushes its own number of trace frames and waits until the other has pushed its frames,
 * so both depths are set when each thread checks its own. Shared state would show the sum of the two. */
static atomic_int threads_pushed;
static int thread_results[2];

typedef struct worker
{
    int slot;
    uint64_t frames;
} worker;

static void run_worker_frames(worker *work)
{
    __oxygen_runtime_initialize(OXYGEN_RUNTIME_EXCEPTIONS_ENABLED);
    for (uint64_t index = 0; index < work->frames; index++)
        __oxygen_runtime_trace_push("frame", "file.oxy", (uint32_t)index, 1);
    atomic_fetch_add(&threads_pushed, 1);
    while (atomic_load(&threads_pushed) < 2)
        ;
    thread_results[work->slot] = __oxygen_runtime_trace_depth() == work->frames;
}

#if defined(_WIN32)
static DWORD WINAPI worker_main(LPVOID argument)
{
    run_worker_frames((worker *)argument);
    return 0;
}
#else
static void *worker_main(void *argument)
{
    run_worker_frames((worker *)argument);
    return NULL;
}
#endif

static int each_thread_sees_only_its_own_trace(void)
{
    worker first = {0, 3};
    worker second = {1, 7};
#if defined(_WIN32)
    HANDLE threads[2];
    threads[0] = CreateThread(NULL, 0, worker_main, &first, 0, NULL);
    threads[1] = CreateThread(NULL, 0, worker_main, &second, 0, NULL);
    CHECK(threads[0] != NULL && threads[1] != NULL);
    WaitForMultipleObjects(2, threads, TRUE, INFINITE);
    CloseHandle(threads[0]);
    CloseHandle(threads[1]);
#else
    pthread_t threads[2];
    CHECK(pthread_create(&threads[0], NULL, worker_main, &first) == 0);
    CHECK(pthread_create(&threads[1], NULL, worker_main, &second) == 0);
    CHECK(pthread_join(threads[0], NULL) == 0);
    CHECK(pthread_join(threads[1], NULL) == 0);
#endif
    CHECK(thread_results[0] == 1);
    CHECK(thread_results[1] == 1);
    return 0;
}

typedef struct test_case
{
    const char *name;
    int (*run)(void);
} test_case;

static const test_case cases[] = {
    {"c-string-from-null", c_string_from_null},
    {"c-string-from-text", c_string_from_text},
    {"string-to-c-string-copies-and-terminates", string_to_c_string_copies_and_terminates},
    {"string-with-embedded-zero-has-no-c-string", string_with_embedded_zero_has_no_c_string},
    {"array-size-within-range", array_size_within_range},
    {"array-size-overflow-stops-the-program", array_size_overflow_stops_the_program},
    {"calloc-zeroes-every-byte", calloc_zeroes_every_byte},
    {"calloc-overflow-stops-the-program", calloc_overflow_stops_the_program},
    {"zero-size-alloc-is-non-null-and-freeable", zero_size_alloc_is_non_null_and_freeable},
    {"trace-stack-grows-past-its-initial-capacity", trace_stack_grows_past_its_initial_capacity},
    {"write-stdout-writes-embedded-zero-bytes", write_stdout_writes_embedded_zero_bytes},
    {"abort-delivers-buffered-stdout", abort_delivers_buffered_stdout},
    {"each-thread-sees-only-its-own-trace", each_thread_sees_only_its_own_trace},
};

int main(int argc, char **argv)
{
    if (argc != 2)
    {
        fprintf(stderr, "usage: runtime_tests <case>\n");
        return 2;
    }
    for (size_t index = 0; index < sizeof(cases) / sizeof(cases[0]); index++)
        if (strcmp(cases[index].name, argv[1]) == 0)
            return cases[index].run();
    fprintf(stderr, "unknown case: %s\n", argv[1]);
    return 2;
}
