#pragma once

#include <algorithm>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>
#include "Diagnostic/Diagnostic.hpp"
#include "Diagnostic/SourceManager.hpp"
#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/SourceLocation.hpp"
#include "Diagnostic/Colors.hpp"

class DiagnosticsEngine {
public:
    DiagnosticsEngine(SourceManager& sm, int contextLines = 1, bool ignoreWarnings = false, bool warningsAreErrors = false)
        : srcMgr(sm), context(contextLines), ignoreWarnings(ignoreWarnings), warningsAsErrors(warningsAreErrors) {}

    void report(const Diagnostic& d) {
        if (d.level == DiagnosticLevel::Warning && ignoreWarnings)
            return;

        Diagnostic newD;
        if (d.level == DiagnosticLevel::Warning && warningsAsErrors) {
            newD.loc = d.loc;
            newD.level = DiagnosticLevel::Error;
            newD.length = d.length;
            newD.message = d.message;
            newD.notes = d.notes;
            newD.suggestion = d.suggestion;
        } else {
            newD = d;
        }

        diags.push_back(newD);
        printDiagnostic(newD);
    }

    bool hasErrors() const {
        for (auto &d : diags) {
            if (d.level == DiagnosticLevel::Error)
                return true;
        }
        return false;
    }

    void setContext(int ctx) { context = ctx; }

private:
    SourceManager& srcMgr;
    int context;
    std::vector<Diagnostic> diags;
    bool ignoreWarnings = false;
    bool warningsAsErrors = false;

    static std::string expandTabs(const std::string& s, unsigned tabSize = 4) {
        std::string out;
        out.reserve(s.size());
        unsigned col = 0;
        for (char ch : s) {
            if (ch == '\t') {
                unsigned spaces = tabSize - (col % tabSize);
                out.append(spaces, ' ');
                col += spaces;
            } else {
                out.push_back(ch);
                ++col;
            }
        }
        return out;
    }

    static const char* levelToString(DiagnosticLevel level) {
        switch (level) {
            case DiagnosticLevel::Info: return "note";
            case DiagnosticLevel::Warning: return "warning";
            case DiagnosticLevel::Error: return "error";
        }
        return "";
    }

    static const char* levelColor(DiagnosticLevel level) {
        switch (level) {
            case DiagnosticLevel::Info: return Colors::Blue;
            case DiagnosticLevel::Warning: return Colors::Yellow;
            case DiagnosticLevel::Error: return Colors::Red;
        }
        return "";
    }

    void printDiagnostic(const Diagnostic& d) {
        // header -> file:line:column: <level>: <message>
        std::ostringstream header;
        header << d.loc.file << ":" << d.loc.line << ":" << d.loc.column << ": ";

        std::string lev = levelToString(d.level);
        const char* color = levelColor(d.level);

        std::cerr << color << header.str() << lev << ": " << Colors::Bold << d.message << Colors::Reset << "\n";

        if (!d.suggestion.empty()) {
            std::cerr << Colors::Gray << "  hint: " << Colors::Reset << d.suggestion << "\n";
        }

        printSourceSnippet(d.loc, d.length);

        for(const auto &note : d.notes) {
            std::ostringstream noteHeader;
            noteHeader << note.loc.file << ":" << note.loc.line << ":" << note.loc.column << ": ";
            std::cerr << Colors::Cyan << noteHeader.str() << "note: " << Colors::Reset << note.message << "\n";
            printSourceSnippet(note.loc, 1);
        }
    }

    void printSourceSnippet(const SourceLocation& loc, int length) {
        const auto& lines = srcMgr.getLines(loc.fileId);
        if(lines.empty()) {
            std::cerr << Colors::Gray << "  (source not available)\n" << Colors::Reset;
            return;
        }

        int totalLines = (int)lines.size();
        int lineIdx = std::max(1, loc.line);
        if (lineIdx > totalLines) {
            std::cerr << Colors::Gray << "  (location outside file: line " << loc.line << " > " << totalLines << ")\n" << Colors::Reset;
            return;
        }

        int start = std::max(1, loc.line - context);
        int end = std::min(totalLines, loc.line + context);

        int width = 1;
        int maxLine = end;
        while(maxLine >= 10) { maxLine /= 10; ++width; }

        for (int L = start; L <= end; ++L) {
            std::string rawLine = lines[L - 1];
            std::string expanded = expandTabs(rawLine);

            std::ostringstream gutter;
            if (L == loc.line) {
                gutter << Colors::Red << ">" << Colors::Reset << " ";
            } else {
                gutter << "  ";
            }

            std::ostringstream ln;
            ln << std::setw(width) << L;

            std::cerr << gutter.str() << Colors::Gray << ln.str() << " | " << Colors::Reset;
            std::cerr << expanded << "\n";

            if (L == loc.line) {
                int col = std::max(1, loc.column);

                std::ostringstream prefixSpaces;
                prefixSpaces << "  " << std::string(width, ' ') << " | ";

                std::cerr << prefixSpaces.str();

                int lineLen = (int)expanded.size();
                int startCol = std::min(col, lineLen + 1);
                int maxAvail = std::max(0, lineLen - (startCol - 1));
                int caretCount = (length > 0) ? std::min(length, std::max(1, maxAvail)) : 1;

                if (startCol > 1)
                    std::cerr << std::string(startCol - 1, ' ');

                std::cerr << Colors::Red << "^";
                for (int i = 1; i < caretCount; ++i) std::cerr << "~";
                std::cerr << Colors::Reset << "\n";
            }
        }
    }
};
