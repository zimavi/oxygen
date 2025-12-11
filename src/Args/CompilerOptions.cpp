#include <iostream>
#include "Args/CompilerOptions.hpp"

CompilerOptions parseArguments(int argc, char** argv) {
    CompilerOptions opts;

    cxxopts::Options options(argv[0], "Oxygen compiler");
    options.positional_help("input file").show_positional_help();

    options.add_options()
        ("h,help", "Print help")

        ("Wignore", "Disable all warnings", cxxopts::value<bool>(opts.wignore))
        ("Werror", "Produce errors instead of warnings", cxxopts::value<bool>(opts.werror))

        ("o", "Output file", cxxopts::value<std::string>(opts.outputFile))
        ("input", "Input file", cxxopts::value<std::string>(opts.inputFile))

        ("context", "Lines of context around diagnostic", cxxopts::value<int>(opts.context)->default_value("1"))

#ifdef DBG_OXY_DUMP_ARGS
        ("dArgs", "Dumps parsed arguments to standard output", cxxopts::value<bool>(opts.dumpArgs))
#endif
; // options.add_options()


    options.parse_positional({"input"});

    auto result = options.parse(argc, argv);

    if (result.count("help")) {
        std::cout << options.help() << "\n";
        exit(0);
    }

    if (!result.count("input")) {
        std::cerr << "No input file provded. Use --help\n";
        exit(1);
    }

    if(opts.dumpArgs) {
        std::cout << "PARSED ARGS:\n";
        std::cout << "  Wignore -> " << opts.wignore << "\n";
        std::cout << "  Werror -> " << opts.werror << "\n";
        std::cout << "  output -> " << opts.outputFile << "\n";
        std::cout << "  input -> " << opts.inputFile << "\n";
        std::cout << "  context -> " << opts.context << "\n";
    }

    return opts;
}
