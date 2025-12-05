#pragma once

#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/DiagnosticsEngine.hpp"
#include <string>
#include <cxxopts.hpp>

struct CompilerOptions {
    bool wall = false;      // Enable basic warnings
    bool wextra = false;    // Enable more warnings
    bool werror = false;    // Produce errors instead of warnings
    std::string inputFile;
    std::string outputFile = "a.out";
};

CompilerOptions parseArguments(int argc, char** argv, DiagnosticsEngine& diag) {
    CompilerOptions opts;

    cxxopts::Options options(argv[0], "Oxygen compiler");
    options.positional_help("input file").show_positional_help();

    options.add_options()
        ("h,help", "Print help")
        ("Wall", "Enable all warnings", cxxopts::value<bool>(opts.wall))
        ("Wextra", "Enable extra warning", cxxopts::value<bool>(opts.wextra))
        ("Werror", "Produce errors instead of warnings", cxxopts::value<bool>(opts.werror))
        ("o", "Output file", cxxopts::value<std::string>(opts.outputFile))
        ("input", "Input file", cxxopts::value<std::string>(opts.outputFile));

    options.parse_positional({"input"});

    auto result = options.parse(argc, argv);

    if (result.count("help")) {
        std::cout << options.help() << "\n";
        exit(0);
    }

    if (!result.count("input")) {
        diag.report(DiagnosticLevel::Error, {"<command-line>", 1, 1}, "No input file provided", "Provide a source file as positional argument");
    }

    return opts;
}
