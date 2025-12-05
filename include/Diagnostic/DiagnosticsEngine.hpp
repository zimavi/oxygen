#pragma once

#include <string>
#include <vector>
#include "Diagnostic/Diagnostic.hpp"
#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/SourceLocation.hpp"

class DiagnosticsEngine {
    std::vector<Diagnostic> diagnostics;
public:
    bool errorWarnings = false;

    void report(DiagnosticLevel level, const SourceLocation& loc, const std::string& msg, const std::string& suggestion = "") {
        auto lvl = level == DiagnosticLevel::Warning ? (errorWarnings ? DiagnosticLevel::Error : level) : level;
        Diagnostic diag {lvl, loc, msg, suggestion};
        diagnostics.push_back(diag);
        diag.print();
    }

    bool hasError() const {
        for (auto& d : diagnostics)
            if (d.level == DiagnosticLevel::Error) return true;
        return false;
    }
};
