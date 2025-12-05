#include "Args/CompilerOptions.hpp"
#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/DiagnosticsEngine.hpp"
#include <iostream>

int main(int argc, char** argv) {
    DiagnosticsEngine diag;
    CompilerOptions opts = parseArguments(argc, argv, diag);

    if (diag.hasError()) {
        std::cerr << "Compilation aborted due to previous errors.\n";
        return 1;
    }

    diag.errorWarnings = opts.werror;

    if (opts.wall) {
        diag.report(DiagnosticLevel::Info, {"main.cpp", 16, 9}, "Wall enabled, all warnings are active");
    }

    std::cout << "Compiling " << opts.inputFile << " -> " << opts.outputFile << "\n";

    diag.report(DiagnosticLevel::Warning, {"main.cpp", 21, 5}, "Sample warning");

    return 0;
}
