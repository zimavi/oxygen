#pragma once

#include "Diagnostic/SourceLocation.hpp"
#include <string>

struct DiagnosticNote {
    SourceLocation loc;
    int length = 1;
    std::string message;
};
