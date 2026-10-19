// Dump disassembly listing for an address range, plus optionally decompile named funcs.
// arg0 = output path
// arg1 = start address hex
// arg2 = end address hex
import java.io.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.Symbol;

public class DumpListing extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String out = args[0];
        Address start = currentProgram.getAddressFactory().getAddress(args[1]);
        Address end = currentProgram.getAddressFactory().getAddress(args[2]);
        StringBuilder sb = new StringBuilder();
        Listing lst = currentProgram.getListing();
        InstructionIterator ii = lst.getInstructions(start, true);
        while (ii.hasNext()) {
            Instruction ins = ii.next();
            if (ins.getAddress().compareTo(end) > 0) break;
            Symbol s = currentProgram.getSymbolTable().getPrimarySymbol(ins.getAddress());
            sb.append(String.format("%s  %-40s ; %s%n", ins.getAddress(), ins.toString(),
                    s == null ? "" : s.getName()));
        }
        try (PrintWriter pw = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) {
            pw.print(sb.toString());
        }
        println("DumpListing written " + out);
    }
}
