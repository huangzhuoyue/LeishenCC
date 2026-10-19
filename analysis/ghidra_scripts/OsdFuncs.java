// OsdFuncs.java - targeted extraction for Hotkey OSD binaries
// args: <outFile> <mode> <param> [maxFuncs]
// modes:
//   str:<substr>      decompile functions that reference a defined string containing substr (case-insensitive)
//   strings:<substr>  list defined strings containing substr + their xrefs (no decompile)
//   index             dump every function: entry/size/calls/referenced strings
//   addr:<0x..[,0x..|0xstart-0xend]>  decompile functions at addresses/ranges
//   func:<substr>     decompile functions whose name contains substr
//   allstrings        dump all defined strings + xrefs
import java.io.*;
import java.util.*;
import java.util.regex.*;
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;

public class OsdFuncs extends GhidraScript {
    static boolean isStr(Data d) {
        String n = d.getDataType().getName().toLowerCase();
        return n.contains("string") || n.contains("unicode") || n.contains("char[") || n.contains("wchar");
    }
    static String strVal(Data d) {
        try { Object v = d.getValue(); return v == null ? null : v.toString(); } catch (Exception e) { return null; }
    }
    Set<Function> funcsForString(String sub) {
        Set<Function> out = new LinkedHashSet<>();
        String needle = sub.toLowerCase();
        Listing lst = currentProgram.getListing();
        DataIterator di = lst.getDefinedData(true);
        while (di.hasNext()) {
            Data d = di.next();
            if (!isStr(d)) continue;
            String v = strVal(d);
            if (v == null || !v.toLowerCase().contains(needle)) continue;
            ReferenceIterator ri = currentProgram.getReferenceManager().getReferencesTo(d.getAddress());
            while (ri.hasNext()) {
                Reference r = ri.next();
                Function f = currentProgram.getFunctionManager().getFunctionContaining(r.getFromAddress());
                if (f != null) out.add(f);
            }
        }
        return out;
    }
    void dumpStrings(String sub, StringBuilder sb) {
        String needle = sub == null ? null : sub.toLowerCase();
        DataIterator di = currentProgram.getListing().getDefinedData(true);
        int n = 0;
        while (di.hasNext()) {
            Data d = di.next();
            if (!isStr(d)) continue;
            String v = strVal(d);
            if (v == null) continue;
            if (needle != null && !v.toLowerCase().contains(needle)) continue;
            sb.append(String.format("%s [%s] %s%n", d.getAddress(), d.getDataType().getName(), v.replace("\n","\\n")));
            ReferenceIterator ri = currentProgram.getReferenceManager().getReferencesTo(d.getAddress());
            while (ri.hasNext()) {
                Reference r = ri.next();
                Function f = currentProgram.getFunctionManager().getFunctionContaining(r.getFromAddress());
                sb.append("    xref ").append(r.getFromAddress()).append(f == null ? "" : ("  in " + f.getName(true) + " @" + f.getEntryPoint())).append("\n");
            }
            n++;
            if (n > 3000) { sb.append("... truncated at 3000 strings\n"); break; }
        }
        sb.append("// total listed=").append(n).append("\n");
    }
    void dumpFunction(Function f, DecompInterface ifc, StringBuilder sb) {
        sb.append("\n//============================================================\n");
        sb.append("// FUNCTION ").append(f.getName(true)).append("  @ ").append(f.getEntryPoint());
        sb.append("  size=").append(f.getBody().getNumAddresses()).append("\n");
        StringBuilder calls = new StringBuilder();
        for (Function c : f.getCalledFunctions(monitor)) calls.append(c.getName(true)).append("@").append(c.getEntryPoint()).append("; ");
        sb.append("// CALLS: ").append(calls).append("\n");
        sb.append("// REFERENCED STRINGS:\n");
        Set<String> seen = new HashSet<>();
        InstructionIterator ii = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (ii.hasNext()) {
            Instruction ins = ii.next();
            for (Reference r : ins.getReferencesFrom()) {
                Address to = r.getToAddress();
                Data d = currentProgram.getListing().getDataAt(to);
                if (d != null && isStr(d)) {
                    String v = strVal(d);
                    if (v != null && seen.add(to.toString())) {
                        sb.append("//   ").append(to).append(": ").append(v.replace("\n","\\n")).append("\n");
                    }
                }
            }
        }
        try {
            DecompileResults res = ifc.decompileFunction(f, 120, monitor);
            if (res != null && res.decompileCompleted() && res.getDecompiledFunction() != null)
                sb.append(res.getDecompiledFunction().getC());
            else sb.append("// DECOMP FAILED: ").append(res == null ? "null" : res.getErrorMessage()).append("\n");
        } catch (Exception e) { sb.append("// EXCEPTION: ").append(e).append("\n"); }
        sb.append("\n");
    }
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String out = args.length > 0 ? args[0] : System.getenv("TEMP") + "\\osdfuncs.txt";
        String mode = args.length > 1 ? args[1] : "index";
        String param = args.length > 2 ? args[2] : "";
        int max = args.length > 3 ? Integer.parseInt(args[3]) : 200;
        StringBuilder sb = new StringBuilder();
        sb.append("// OsdFuncs program=").append(currentProgram.getName()).append(" mode=").append(mode).append(" param=").append(param).append("\n");
        DecompInterface ifc = new DecompInterface();
        ifc.toggleCCode(true); ifc.toggleSyntaxTree(true); ifc.openProgram(currentProgram);
        Set<Function> targets = new LinkedHashSet<>();
        if (mode.equals("index")) {
            FunctionManager fm = currentProgram.getFunctionManager();
            int n = 0;
            for (Function f : fm.getFunctions(true)) {
                sb.append("F ").append(f.getEntryPoint()).append(" size=").append(f.getBody().getNumAddresses()).append(" ").append(f.getName(true));
                StringBuilder calls = new StringBuilder();
                for (Function c : f.getCalledFunctions(monitor)) calls.append(c.getName(true)).append("@").append(c.getEntryPoint()).append(";");
                sb.append(" CALLS=").append(calls).append("\n");
                InstructionIterator ii = currentProgram.getListing().getInstructions(f.getBody(), true);
                while (ii.hasNext()) {
                    Instruction ins = ii.next();
                    for (Reference r : ins.getReferencesFrom()) {
                        Data d = currentProgram.getListing().getDataAt(r.getToAddress());
                        if (d != null && isStr(d)) {
                            String v = strVal(d);
                            if (v != null) sb.append("   S ").append(r.getToAddress()).append(" ").append(v.replace("\n","\\n")).append("\n");
                        }
                    }
                }
                n++;
            }
            sb.append("// functions=").append(n).append("\n");
        } else if (mode.startsWith("strings:")) {
            dumpStrings(param, sb);
        } else if (mode.equals("allstrings")) {
            dumpStrings(null, sb);
        } else if (mode.startsWith("str:")) {
            targets = funcsForString(param);
        } else if (mode.startsWith("func:")) {
            String p = param.toLowerCase();
            for (Function f : currentProgram.getFunctionManager().getFunctions(true))
                if (f.getName(true).toLowerCase().contains(p)) targets.add(f);
        } else if (mode.startsWith("addr:")) {
            FunctionManager fm = currentProgram.getFunctionManager();
            for (String part : param.split(",")) {
                part = part.trim();
                if (part.contains("-")) {
                    String[] ab = part.split("-");
                    Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(ab[0].trim());
                    Address b = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(ab[1].trim());
                    for (Function f : fm.getFunctions(a, true)) { if (f.getEntryPoint().compareTo(b) > 0) break; targets.add(f); }
                } else {
                    Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(part);
                    Function f = fm.getFunctionContaining(a);
                    if (f != null) targets.add(f);
                }
            }
        }
        if (!mode.equals("index") && !mode.startsWith("strings:") && !mode.equals("allstrings")) {
            sb.append("// targetFunctions=").append(targets.size()).append("\n");
            int done = 0;
            for (Function f : targets) {
                if (done >= max) { sb.append("// max reached\n"); break; }
                dumpFunction(f, ifc, sb);
                done++;
            }
        }
        try (PrintWriter pw = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) { pw.print(sb.toString()); }
        println("OsdFuncs written " + out + " (" + sb.length() + " chars)");
    }
}
