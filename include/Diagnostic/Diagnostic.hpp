#pragma once

#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/SourceLocation.hpp"
#include <iostream>
#include <string>

struct Diagnostic {
    DiagnosticLevel level;
    SourceLocation loc;
    std::string message;
    std::string suggestion;

    void print() const {
        std::string prefix;
        switch (level) {
            case DiagnosticLevel::Info: prefix = "info"; break;
            case DiagnosticLevel::Warning: prefix = "warning"; break;
            case DiagnosticLevel::Error: prefix = "error"; break;
        }

        std::cerr << loc.toString() << ": " << prefix << ": " << message << "\n";
        if (!suggestion.empty()) {
            std::cerr << "  suggestion: " << suggestion << "\n";
        }
    }
};
