#pragma once

#include <sstream>
#include <string>

struct SourceLocation {
    std::string file;
    int line;
    int column;

    std::string toString() const {
        std::ostringstream oss;
        oss << file << ":" << line << ":" << column;
        return oss.str();
    }
};
