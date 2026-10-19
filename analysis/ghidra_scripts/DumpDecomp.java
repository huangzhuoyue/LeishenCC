// Dump decompiled C for functions.
// arg0 = output path
// arg1 = mode:
//        "all"                -> every function
//        "callers:<substr>"   -> functions whose body calls a symbol whose name contains substr (also decompile callee)
//        "regex:<pattern>"    -> function name matches regex
// arg2 = optional max function count (default 100000)
import java.io.*;
import java.util.*;
import java.util.regex.*;
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.address.Address;

public class DumpDecomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String out = args.length > 0 ? args[0] : System.getenv("TEMP") + "\\dumpdecomp.c";
        String mode = args.length > 1 ? args[1] : "all";
        int max = args.length > 2 ? Integer.parseInt(args[2]) : 100000;

        Set<Function> targets = new LinkedHashSet<>();
        FunctionManager fm = currentProgram.getFunctionManager();
        if (mode.equals("all")) {
            for (Function f : fm.getFunctions(true)) targets.add(f);
        } else if (mode.startsWith("callers:")) {
            String sub = mode.substring("callers:".length()).toLowerCase();
            for (Function f : fm.getFunctions(true)) {
                boolean hit = false;
                for (Function callee : f.getCalledFunctions(monitor)) {
                    String nm = callee.getName(true).toLowerCase();
                    if (nm.contains(sub)) { hit = true; break; }
                }
                if (hit) { targets.add(f); }
            }
            // also the matching callees themselves
            for (Function f : fm.getFunctions(true)) {
                if (f.getName(true).toLowerCase().contains(sub)) targets.add(f);
            }
        } else if (mode.startsWith("content:")) {
            // functions that reference a defined string/data whose value contains substring
            String sub = mode.substring("content:".length()).toLowerCase();
            java.util.Set<Address> addrs = new java.util.HashSet<>();
            ghidra.program.model.listing.DataIterator di = currentProgram.getListing().getDefinedData(true);
            while (di.hasNext()) {
                ghidra.program.model.listing.Data d = di.next();
                Object v = null;
                try { v = d.getValue(); } catch (Exception e) { }
                if (v != null && v.toString().toLowerCase().contains(sub)) addrs.add(d.getAddress());
            }
            // raw memory search fallback for non-defined strings
            println("content targets=" + addrs.size() + " for " + sub);
            ghidra.program.model.symbol.ReferenceManager rm = currentProgram.getReferenceManager();
            for (Address a : addrs) {
                ghidra.program.model.symbol.ReferenceIterator ri = rm.getReferencesTo(a);
                while (ri.hasNext()) {
                    ghidra.program.model.symbol.Reference r = ri.next();
                    Function f = fm.getFunctionContaining(r.getFromAddress());
                    if (f != null) targets.add(f);
                }
            }
        } else if (mode.startsWith("refs:")) {
            // functions that reference a symbol whose full name contains substring
            String sub = mode.substring("refs:".length()).toLowerCase();
            java.util.Set<Address> addrs = new java.util.HashSet<>();
            SymbolIterator si = currentProgram.getSymbolTable().getAllSymbols(true);
            while (si.hasNext()) {
                Symbol s = si.next();
                if (s.getName(true).toLowerCase().contains(sub)) addrs.add(s.getAddress());
            }
            println("refs targets=" + addrs.size() + " for " + sub);
            ghidra.program.model.symbol.ReferenceManager rm = currentProgram.getReferenceManager();
            for (Address a : addrs) {
                ghidra.program.model.symbol.ReferenceIterator ri = rm.getReferencesTo(a);
                while (ri.hasNext()) {
                    ghidra.program.model.symbol.Reference r = ri.next();
                    Function f = fm.getFunctionContaining(r.getFromAddress());
                    if (f != null) targets.add(f);
                }
            }
            // also top-level function named like the symbol
            for (Function f : fm.getFunctions(true)) {
                if (f.getName(true).toLowerCase().contains(sub)) targets.add(f);
            }
        } else if (mode.startsWith("namesfile:")) {
            String fpath = mode.substring("namesfile:".length());
            java.util.Set<String> wanted = new java.util.HashSet<>();
            for (String line : java.nio.file.Files.readAllLines(java.nio.file.Paths.get(fpath))) {
                if (!line.trim().isEmpty()) wanted.add(line.trim().toLowerCase());
            }
            for (Function f : fm.getFunctions(true)) {
                if (wanted.contains(f.getName().toLowerCase()) || wanted.contains(f.getName(true).toLowerCase())) {
                    targets.add(f);
                }
            }
        } else if (mode.startsWith("names:")) {
            String[] names = mode.substring("names:".length()).split(",");
            java.util.Set<String> wanted = new java.util.HashSet<>();
            for (String n : names) wanted.add(n.trim().toLowerCase());
            for (Function f : fm.getFunctions(true)) {
                if (wanted.contains(f.getName().toLowerCase()) || wanted.contains(f.getName(true).toLowerCase())) {
                    targets.add(f);
                }
            }
        } else if (mode.startsWith("regex:")) {
            Pattern pat = Pattern.compile(mode.substring("regex:".length()));
            for (Function f : fm.getFunctions(true)) {
                if (pat.matcher(f.getName(true)).find()) targets.add(f);
            }
        }

        DecompInterface ifc = new DecompInterface();
        ifc.toggleCCode(true);
        ifc.toggleSyntaxTree(true);
        ifc.openProgram(currentProgram);

        StringBuilder sb = new StringBuilder();
        sb.append("// decompiled by DumpDecomp mode=").append(mode)
          .append(" targets=").append(targets.size()).append("\n");
        int done = 0;
        for (Function f : targets) {
            if (done >= max) { sb.append("\n// ...max reached at ").append(max).append("\n"); break; }
            sb.append("\n//============================================================\n");
            sb.append("// FUNCTION ").append(f.getName(true)).append("  @ ").append(f.getEntryPoint());
            sb.append("  size=").append(f.getBody().getNumAddresses());
            // calls
            StringBuilder calls = new StringBuilder();
            for (Function c : f.getCalledFunctions(monitor)) calls.append(c.getName(true)).append("; ");
            sb.append("\n// CALLS: ").append(calls).append("\n");
            try {
                DecompileResults r = ifc.decompileFunction(f, 120, monitor);
                if (r != null && r.decompileCompleted() && r.getDecompiledFunction() != null) {
                    sb.append(r.getDecompiledFunction().getC());
                } else {
                    sb.append("// DECOMP FAILED: ").append(r == null ? "null" : r.getErrorMessage()).append("\n");
                }
            } catch (Exception e) {
                sb.append("// EXCEPTION: ").append(e).append("\n");
            }
            sb.append("\n");
            done++;
            if (done % 25 == 0) println("decompiled " + done);
        }
        try (PrintWriter pw = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) {
            pw.print(sb.toString());
        }
        println("DumpDecomp written: " + out + " funcs=" + done);
    }
}
