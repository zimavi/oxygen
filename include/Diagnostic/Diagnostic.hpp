#pragma once

#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/SourceLocation.hpp"
#include "Diagnostic/DiagnosticNote.hpp"
#include <string>
#include <vector>

struct Diagnostic {
    DiagnosticLevel level;
    SourceLocation loc;
    std::string message;
    std::string suggestion;
    int length = 1;
    std::vector<DiagnosticNote> notes;
};
