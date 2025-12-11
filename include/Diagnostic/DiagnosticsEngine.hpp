#pragma once

#include <string>
#include <vector>
#include "Diagnostic/Diagnostic.hpp"
#include "Diagnostic/SourceManager.hpp"
#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/SourceLocation.hpp"

class DiagnosticsEngine {
public:
    DiagnosticsEngine(SourceManager& sm, int contextLines = 1, bool ignoreWarnings = false, bool warningsAreErrors = false)
        : srcMgr(sm), context(contextLines), ignoreWarnings(ignoreWarnings), warningsAsErrors(warningsAreErrors) {}

    void report(const Diagnostic& d);

    bool hasErrors() const;

    void setContext(int ctx) { context = ctx; }

private:
    SourceManager& srcMgr;
    int context;
    std::vector<Diagnostic> diags;
    bool ignoreWarnings = false;
    bool warningsAsErrors = false;

    std::string expandTabs(const std::string& s, unsigned tabSize = 4);

    const char* levelToString(DiagnosticLevel level);

    const char* levelColor(DiagnosticLevel level);

    void printDiagnostic(const Diagnostic& d);

    void printSourceSnippet(const SourceLocation& loc, int length);
};
