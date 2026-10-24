/* Native tests for runtime functions that Oxygen programs cannot reach directly. The program runs one named case, given on the
 * command line, so a case that ends the process (a runtime failure) can be checked by its exit code. A passing case returns 0. A
 * failing check prints its line and returns 1. An unknown case name returns 2.
 *
 * Build: clang -I runtime/include runtime/tests/runtime_tests.c liboxygenrt.a -o runtime_tests */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stddef.h>
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

static int destructions;

static void count_destruction(void *object)
{
    (void)object;
    destructions++;
}

static const oxygen_runtime_type_info counted_type = {
    .name = "counted",
    .destructor = count_destruction,
};

/* A counted object whose first word names its runtime type, as the compiler lays objects out. */
static void *new_counted_object(void)
{
    void *object = oxygen_object_new(sizeof(void *));
    *(const oxygen_runtime_type_info **)object = &counted_type;
    return object;
}

static int object_new_counts_one_reference_before_the_object(void)
{
    void *object = oxygen_object_new(16);
    CHECK(((uint64_t *)object)[-1] == 1);
    CHECK(((uintptr_t)object % sizeof(uint64_t)) == 0);
    oxygen_object_free(object);
    return 0;
}

static int object_release_runs_the_destructor_once_at_zero(void)
{
    destructions = 0;
    void *object = new_counted_object();
    oxygen_retain(object);
    CHECK(((uint64_t *)object)[-1] == 2);
    oxygen_release(object);
    CHECK(destructions == 0);
    oxygen_release(object);
    CHECK(destructions == 1);
    return 0;
}

static int null_object_operations_do_nothing(void)
{
    oxygen_retain(NULL);
    oxygen_release(NULL);
    oxygen_object_free(NULL);
    return 0;
}

static int release_of_a_stack_object_does_nothing(void)
{
    destructions = 0;
    /* The word before a stack object holds the stack count, as the compiler lays it out. */
    uint64_t block[3] = {OXYGEN_STACK_OBJECT_COUNT, 0, 0};
    void *object = &block[1];
    *(const oxygen_runtime_type_info **)object = &counted_type;
    oxygen_retain(object);
    oxygen_release(object);
    oxygen_release(object);
    CHECK(destructions == 0);
    CHECK(block[0] == OXYGEN_STACK_OBJECT_COUNT);
    return 0;
}

static int release_without_a_count_stops_the_program(void)
{
    void *object = new_counted_object();
    ((uint64_t *)object)[-1] = 0;
    oxygen_release(object);
    return 1;
}

typedef struct test_case
{
    const char *name;
    int (*run)(void);
} test_case;

/* An exception object as the compiler lays it out: its type record, then its message field. */
typedef struct test_exception
{
    const oxygen_runtime_type_info *type;
    oxygen_string message;
} test_exception;

static const oxygen_runtime_type_info index_failure_type = {
    .name = "std.exception.IndexOutOfRangeException",
    .message_offset = offsetof(test_exception, message),
};

static const oxygen_runtime_type_info target_type = {
    .name = "program.Leaf",
};

static const oxygen_runtime_type_info object_type = {
    .name = "program.Other",
};

static int text_is(oxygen_string text, const char *expected)
{
    return text.length == strlen(expected) && memcmp(text.data, expected, (size_t)text.length) == 0;
}

static int failure_message_is_set_on_the_exception_object(void)
{
    test_exception error = {&index_failure_type, {NULL, 0}};
    __oxygen_runtime_set_failure_message(&error, OXYGEN_FAILURE_FRAME_INDEX, 9, 1);
    CHECK(text_is(error.message, "Stack trace frame 9 does not exist: the trace has 1 frames."));
    return 0;
}

static int cast_failure_names_the_object_and_its_target(void)
{
    oxygen_string text = {NULL, 0};
    test_exception other = {&object_type, {NULL, 0}};
    __oxygen_runtime_format_failure(OXYGEN_FAILURE_INVALID_CAST, (uint64_t)(uintptr_t)&other,
        (uint64_t)(uintptr_t)&target_type, &text);
    CHECK(text_is(text, "Cannot cast an object of type program.Other to program.Leaf, because it is not an instance of program.Leaf."));
    __oxygen_runtime_format_failure(OXYGEN_FAILURE_INVALID_CAST, 0, (uint64_t)(uintptr_t)&target_type, &text);
    CHECK(text_is(text, "Cannot cast a null reference to program.Leaf."));
    return 0;
}

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
    {"object-new-counts-one-reference-before-the-object", object_new_counts_one_reference_before_the_object},
    {"object-release-runs-the-destructor-once-at-zero", object_release_runs_the_destructor_once_at_zero},
    {"null-object-operations-do-nothing", null_object_operations_do_nothing},
    {"release-of-a-stack-object-does-nothing", release_of_a_stack_object_does_nothing},
    {"release-without-a-count-stops-the-program", release_without_a_count_stops_the_program},
    {"failure-message-is-set-on-the-exception-object", failure_message_is_set_on_the_exception_object},
    {"cast-failure-names-the-object-and-its-target", cast_failure_names_the_object_and_its_target},
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
