// Dump PE-level info: memory blocks, entry points, imports (external symbols),
// exported/global symbols, defined strings. Arg0 = output file path.
import java.io.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.listing.*;

public class DumpInfo extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String out = args.length > 0 ? args[0] : System.getenv("TEMP") + "\\dumpinfo.txt";
        StringBuilder sb = new StringBuilder();
        Program p = currentProgram;
        sb.append("=== PROGRAM ===\n");
        sb.append("name=").append(p.getName()).append("\n");
        sb.append("exePath=").append(p.getExecutablePath()).append("\n");
        sb.append("format=").append(p.getExecutableFormat()).append("\n");
        sb.append("language=").append(p.getLanguage().getLanguageID()).append("\n");
        sb.append("imageBase=").append(p.getImageBase()).append("\n");
        sb.append("minAddr=").append(p.getMinAddress()).append(" maxAddr=").append(p.getMaxAddress()).append("\n");
        sb.append("md5=").append(p.getExecutableMD5()).append("\n");

        sb.append("\n=== MEMORY BLOCKS ===\n");
        for (MemoryBlock b : p.getMemory().getBlocks()) {
            sb.append(String.format("%s-%s %-24s perm=%s%s%s init=%s size=0x%x%n",
                b.getStart(), b.getEnd(), b.getName(),
                b.isRead()?"R":"-", b.isWrite()?"W":"-", b.isExecute()?"X":"-",
                b.isInitialized(), b.getSize()));
        }

        sb.append("\n=== ENTRY POINTS ===\n");
        for (Address a : p.getSymbolTable().getExternalEntryPointIterator()) {
            sb.append(a).append("  ").append(getSymbolAt(a)).append("\n");
        }

        sb.append("\n=== IMPORTS (external symbols) ===\n");
        SymbolIterator ext = p.getSymbolTable().getExternalSymbols();
        while (ext.hasNext()) {
            Symbol s = ext.next();
            sb.append(s.getAddress()).append("  ").append(s.getName(true)).append("\n");
        }

        sb.append("\n=== EXTERNAL LOCATIONS (IAT thunks) ===\n");
        // thunk functions whose body jumps to external
        for (Function f : p.getFunctionManager().getFunctions(true)) {
            if (f.isThunk()) {
                sb.append(f.getEntryPoint()).append("  THUNK ").append(f.getName())
                  .append(" -> ").append(f.getThunkedFunction(true) != null ? f.getThunkedFunction(true).getName(true) : "?").append("\n");
            }
        }

        sb.append("\n=== FUNCTIONS (count) ===\n");
        int n = 0;
        for (Function f : p.getFunctionManager().getFunctions(true)) n++;
        sb.append("functionCount=").append(n).append("\n");

        sb.append("\n=== GLOBAL/EXPORTED SYMBOLS ===\n");
        SymbolIterator all = p.getSymbolTable().getAllSymbols(true);
        while (all.hasNext()) {
            Symbol s = all.next();
            if (s.isExternal()) continue;
            if (s.getSymbolType() != SymbolType.FUNCTION && s.getSymbolType() != SymbolType.LABEL) continue;
            if (s.getName().startsWith("FUN_") || s.getName().startsWith("LAB_") || s.getName().startsWith("DAT_") || s.getName().startsWith("UNK_")) continue;
            sb.append(s.getAddress()).append("  ").append(s.getName(true)).append("  type=").append(s.getSymbolType()).append("\n");
        }

        sb.append("\n=== DEFINED STRINGS ===\n");
        Listing lst = p.getListing();
        DataIterator di = lst.getDefinedData(true);
        int sc = 0;
        while (di.hasNext()) {
            Data d = di.next();
            String dt = d.getDataType().getName();
            if (dt.contains("string") || dt.contains("String") || dt.contains("unicode") || dt.contains("Unicode")) {
                Object v = null;
                try { v = d.getValue(); } catch (Exception e) { }
                sb.append(d.getAddress()).append("  [").append(dt).append("]  ").append(v).append("\n");
                sc++;
                if (sc > 4000) { sb.append("...truncated...\n"); break; }
            }
        }
        sb.append("stringCount=").append(sc).append("\n");

        try (PrintWriter pw = new PrintWriter(new OutputStreamWriter(new FileOutputStream(out), "UTF-8"))) {
            pw.print(sb.toString());
        }
        println("DumpInfo written to " + out);
    }
}
