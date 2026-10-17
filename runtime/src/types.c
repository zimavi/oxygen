#include <stdlib.h>
#include <string.h>
#include "oxygen_runtime.h"

typedef struct oxygen_type_relation
{
    const oxygen_runtime_type_info *actual_type;
    const oxygen_runtime_type_info *parent_type;
    struct oxygen_type_relation *next;
} oxygen_type_relation;

static oxygen_type_relation *oxygen_type_relations;

static int oxygen_type_matches(
    const oxygen_runtime_type_info *left,
    const oxygen_runtime_type_info *right)
{
    return left != NULL
        && right != NULL
        && left->name != NULL
        && right->name != NULL
        && strcmp(left->name, right->name) == 0;
}

uint8_t __oxygen_runtime_is_instance(
    const void *object,
    const oxygen_runtime_type_info *expected_type)
{
    if (object == NULL || expected_type == NULL)
        return 0;

    const oxygen_runtime_type_info *actual_type =
        *(const oxygen_runtime_type_info *const *)object;
    if (actual_type == NULL)
        return 0;
    if (oxygen_type_matches(actual_type, expected_type))
        return 1;

    const oxygen_runtime_type_info *visited[128];
    size_t visited_count = 0;
    visited[visited_count++] = actual_type;
    for (size_t index = 0; index < visited_count; index++)
    {
        const oxygen_runtime_type_info *current = visited[index];
        for (oxygen_type_relation *relation = oxygen_type_relations;
             relation != NULL;
             relation = relation->next)
        {
            if (!oxygen_type_matches(relation->actual_type, current))
                continue;
            if (oxygen_type_matches(relation->parent_type, expected_type))
                return 1;
            if (visited_count < sizeof(visited) / sizeof(visited[0]))
            {
                int already_visited = 0;
                for (size_t visited_index = 0; visited_index < visited_count; visited_index++)
                {
                    if (oxygen_type_matches(visited[visited_index], relation->parent_type))
                    {
                        already_visited = 1;
                        break;
                    }
                }
                if (!already_visited)
                    visited[visited_count++] = relation->parent_type;
            }
        }
    }
    return 0;
}

void __oxygen_runtime_register_type(
    oxygen_runtime_type_info *actual_type,
    const oxygen_runtime_type_info *parent_type)
{
    if (!oxygen_type_matches(actual_type, actual_type)
        || !oxygen_type_matches(parent_type, parent_type)
        || oxygen_type_matches(actual_type, parent_type))
        return;

    for (oxygen_type_relation *relation = oxygen_type_relations;
         relation != NULL;
         relation = relation->next)
    {
        if (oxygen_type_matches(relation->actual_type, actual_type)
            && oxygen_type_matches(relation->parent_type, parent_type))
            return;
    }

    oxygen_type_relation *relation = malloc(sizeof(*relation));
    if (relation == NULL)
        return;
    relation->actual_type = actual_type;
    relation->parent_type = parent_type;
    relation->next = oxygen_type_relations;
    oxygen_type_relations = relation;
}
