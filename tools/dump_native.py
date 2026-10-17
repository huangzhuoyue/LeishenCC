# Ghidra headless post-script: dump exports, imports, and decompile functions
# that reference DeviceIoControl / CreateFileW / registry / WMI.
# @category Leishen
import os
from ghidra.app.decompiler import DecompInterface
from ghidra.util.task import ConsoleTaskMonitor
from ghidra.program.model.symbol import SymbolType

prog = currentProgram
outdir = os.environ.get("LS_OUT", "C:/Users/Administrator/Documents/LeishenReverse/ghidra_out")
if not os.path.isdir(outdir):
    os.makedirs(outdir)
base = prog.getName()
out = open(os.path.join(outdir, base + ".txt"), "w")

out.write("=== PROGRAM %s ===\n" % base)
out.write("image base: %s\n" % prog.getImageBase())

st = prog.getSymbolTable()
out.write("\n=== EXPORTS ===\n")
for s in st.getAllSymbols(True):
    if s.isExternalEntryPoint() or s.getSymbolType() == SymbolType.FUNCTION:
        if s.isGlobal():
            out.write("%s  %s\n" % (s.getAddress(), s.getName()))

out.write("\n=== IMPORTS ===\n")
ext = prog.getExternalManager()
for name in ext.getExternalLibraryNames():
    out.write("[lib] %s\n" % name)
    for e in ext.getExternalLocations(name):
        out.write("   %s\n" % e.getName())

# functions referencing interesting APIs
interesting = ["DeviceIoControl", "CreateFileW", "WriteFile", "ReadFile",
               "RegSetValueExW", "RegQueryValueExW", "NtOpenSection",
               "ZwDeviceIoControlFile"]

fm = prog.getFunctionManager()
decomp = DecompInterface()
decomp.openProgram(prog)
monitor = ConsoleTaskMonitor()

targets = set()
refs = prog.getReferenceManager()
for f in fm.getFunctions(True):
    body = f.getBody()
    for api in interesting:
        for sym in prog.getSymbolTable().getSymbols(api):
            for r in refs.getReferencesTo(sym.getAddress()):
                if body.contains(r.getFromAddress()):
                    targets.add(f)

out.write("\n=== FUNCS touching IO/registry: %d ===\n" % len(targets))
for f in sorted(targets, key=lambda x: x.getEntryPoint().getOffset()):
    out.write("\n---- %s @ %s ----\n" % (f.getName(), f.getEntryPoint()))
    res = decomp.decompileFunction(f, 60, monitor)
    if res and res.decompileCompleted():
        out.write(res.getDecompiledFunction().getC())
    else:
        out.write("(decompile failed)\n")

# strings with GUID/device/ioctl hints
out.write("\n=== STRING HINTS ===\n")
from ghidra.program.util import DefinedDataIterator
for d in DefinedDataIterator.definedStrings(prog):
    v = d.getValue()
    if v is None:
        continue
    s = str(v)
    low = s.lower()
    if any(k in low for k in ["\\\\", "ccdrv", "ioctl", "wmi", "control", "fan", "led", "quanta", "sdk"]):
        out.write("%s  %s\n" % (d.getAddress(), s))

out.close()
print("WROTE " + out.name)
