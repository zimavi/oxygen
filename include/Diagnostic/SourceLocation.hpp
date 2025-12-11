#pragma once

#include <string>

struct SourceLocation {
    std::string file;
    int fileId = -1;
    int line;
    int column;
};
