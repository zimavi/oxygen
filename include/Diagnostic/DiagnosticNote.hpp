#pragma once

#include "Diagnostic/SourceLocation.hpp"
#include <string>

struct DiagnosticNote {
    SourceLocation loc;
    std::string message;
};
