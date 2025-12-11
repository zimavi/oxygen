#define DBG_OXY_DUMP_ARGS

#include "Args/CompilerOptions.hpp"
#include "Diagnostic/Diagnostic.hpp"
#include "Diagnostic/DiagnosticLevel.hpp"
#include "Diagnostic/DiagnosticsEngine.hpp"
#include "Diagnostic/SourceManager.hpp"

int main(int argc, char** argv) {
    CompilerOptions opts = parseArguments(argc, argv);
    SourceManager sm;
    DiagnosticsEngine diag(sm, opts.context, opts.wignore, opts.werror);

    auto mainId = sm.loadFile(opts.inputFile);

    if (!sm.exists(mainId)) {
        Diagnostic d;
        d.level = DiagnosticLevel::Error;
        d.loc = { opts.inputFile, 1, 1 };
        d.message = "cannot open input file";
        d.length = 6;
        d.suggestion = "make sure file exists and you have permissions to read it";
        diag.report(d);
        return 1;
    }

    {
        Diagnostic w;
        w.level = DiagnosticLevel::Warning;
        w.loc = { opts.inputFile, mainId, 1, 8 };
        w.message = "unable to resolve 'std.io'";
        w.length = 6;
        w.suggestion = "is it spelled corectly?";
        diag.report(w);
    }

    {
        Diagnostic d;
        d.level = DiagnosticLevel::Error;
        d.loc = { opts.inputFile, mainId, 4, 5 };
        d.message = "Unexpected identifier 'println'";
        d.length = 7;
        d.suggestion = "check if it is defined, or module is imported";

        d.notes.push_back({ { opts.inputFile, mainId, 1, 8 }, 6, "this unresovled import might contain identifier" });
        diag.report(d);
    }

    return 0;
}
