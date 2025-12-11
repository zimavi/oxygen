#pragma once

#include <string>
#include <cxxopts.hpp>

struct CompilerOptions {
    bool werror = false;    // Produce errors instead of warnings
    bool wignore = false;   // Disable all warnings

    std::string inputFile;
    std::string outputFile = "a.out";

    int context = 1;

    /* -=-=-=- DEBUG -=-=-=- */
    bool dumpArgs = false;
};

CompilerOptions parseArguments(int argc, char** argv);
