// Find data locations containing a pointer to given function entry points (vtable hunting).
// arg0 = output file; arg1 = namesfile with function entry addresses (hex, one per line, e.g. 1402e3960)
import java.io.*;
import java.util.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;

public class VtableFind extends GhidraScript {
    String resolve(long v) {
        Address a = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(v);
        Function f = currentProgram.getFunctionManager().getFunctionAt(a);
        if (f != null) return f.getName(true);
        Symbol s = currentProgram.getSymbolTable().getPrimarySymbol(a);
        if (s != null) return s.getName(true);
        return "";
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String out = args[0];
        List<Long> targets = new ArrayList<>();
        for (String line : java.nio.file.Files.readAllLines(java.nio.file.Paths.get(args[1]))) {
            line = line.trim();
            if (!line.isEmpty()) targets.add(Long.parseUnsignedLong(line, 16));
        }
        StringBuilder sb = new StringBuilder();
        Memory mem = currentProgram.getMemory();
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isInitialized() || !b.isRead() || b.isExecute()) continue;
            long start = b.getStart().getOffset();
            long end = b.getEnd().getOffset();
            for (long a = (start + 7) & ~7L; a + 8 <= end; a += 8) {
                long v;
                try { v = mem.getLong(currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(a)); }
                catch (Exception e) { continue; }
                if (!targets.contains(v)) continue;
                sb.append("=== vtable ptr candidate @ ").append(Long.toHexString(a))
                  .append(" -> ").append(Long.toHexString(v)).append(" ").append(resolve(v)).append("\n");
                for (int k = -4; k <= 10; k++) {
                    long off = a + (long)k * 8;
                    if (off < start || off + 8 > end) continue;
                    long pv;
                    try { pv = mem.getLong(currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(off)); }
                    catch (Exception e) { continue; }
                    String r = resolve(pv);
                    sb.append(String.format("   [%+3d] %s  %016x  %s%n", k, Long.toHexString(off), pv, r));
                }
                sb.append("\n");
            }
        }
        try (PrintWriter pw = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) {
            pw.print(sb.toString());
        }
        println("VtableFind written " + out);
    }
}
