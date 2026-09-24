#!/usr/bin/env python3
"""Generate "MiYue Player.umc" from the I/O declarations of "MiYue Player v1.0.usp".

The SIMPL Windows text format written here is modelled on a real SIMPL Windows 4.02 program
(the legacy MiYue MIYUE.smw): bracketed objects, ObjTp=Sm symbols (SmC=157 folder, SmC=156
Logic, SmC=103 SIMPL+ module with I<n>/O<n>/P<n> handles, n1I/n2I/mI/n1O/mO/tO counts) and
ObjTp=Sg signals (no SgTp = digital, SgTp=2 analog, SgTp=4 serial).

NOT verified: the FSgntr/Hd values for a *user macro*, and the macro argument definition.
Real .umc files expose their pins through a "DefineArguments" argument symbol whose object
layout is not known here, so that folder is left empty: after opening the file in SIMPL
Windows the integrator adds the arguments (README "Rebuilding the macro"). Signals carry the
pin names so that step is a drag-and-drop.

Usage:  python gen_umc.py   (writes MiYue Player.umc next to this script, CRLF line ends)
"""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
USP = os.path.join(HERE, "..", "simplplus", "MiYue Player v1.0.usp")
OUT = os.path.join(HERE, "MiYue Player.umc")

# parameter defaults, in declaration order (STRING_PARAMETER first, then INTEGER_PARAMETER)
PARAM_DEFAULTS = {"IP_Address": "", "Poll_Interval": "3d", "Volume_Step": "5d", "Debug_Mode": "0d", "Unicode_Text": "1d"}


def declarations(text, kind):
    """Names declared by e.g. DIGITAL_INPUT a, b, c[16]; arrays are expanded."""
    names = []
    for m in re.finditer(r"^\s*" + kind + r"\s+(.*?);", text, re.S | re.M):
        for part in m.group(1).replace("\n", " ").split(","):
            part = part.strip()
            if not part:
                continue
            am = re.match(r"(\w+)\s*\[\s*(\w+)\s*\]", part)
            if am:
                size = am.group(2)
                if not size.isdigit():
                    size = re.search(r"#DEFINE_CONSTANT\s+" + size + r"\s+(\d+)", text).group(1)
                if kind.startswith("STRING_INPUT"):
                    names.append(am.group(1))          # STRING_INPUT x[255] = length, not an array
                else:
                    names += ["%s[%d]" % (am.group(1), i) for i in range(1, int(size) + 1)]
            else:
                names.append(part)
    return names


def block(lines):
    return "[\r\n" + "\r\n".join(lines) + "\r\n]\r\n"


def main():
    text = open(USP, encoding="ascii").read()
    di = declarations(text, "DIGITAL_INPUT")
    ai = declarations(text, "ANALOG_INPUT")
    si = declarations(text, "STRING_INPUT")
    do = declarations(text, "DIGITAL_OUTPUT")
    ao = declarations(text, "ANALOG_OUTPUT")
    so = declarations(text, "STRING_OUTPUT")
    params = re.findall(r"^STRING_PARAMETER\s+(\w+)", text, re.M)
    params += [p.strip() for p in re.search(r"^INTEGER_PARAMETER\s+(.*?);", text, re.M).group(1).split(",")]

    out = []
    out.append(block(["Version=1"]))
    out.append(block(["ObjTp=FSgntr", "Sgntr=SimplWindow", "RelVrs=4.02.38", "IntStrVrs=2",
                      "MinSMWVrs=3.00.00", "MinTIOVrs=670", "SavedBy=SMW4.02.20"]))
    out.append(block(["ObjTp=Hd", "S0Nd=1", "S1Nd=2", "SLNd=3", "PrNm=MiYue Player.umc", "SmVr=883", "DvVr=883",
                      "TpN1=1", "TpN2=2", "TpN3=3", "TpN4=4", "TpN5=5", "APg=1", "FltTmp=1", "FpCS=0",
                      "EnType=0", "ZeroOnIoOk=0", "PIT=MiYue Player"]))
    out.append(block(["ObjTp=Bk", "Nm1=\\", "Sx1=0", "Sy1=0", "Mx1=1"]))

    # signals: handles 1..3 are reserved by the header (0, 1, local); ours start at 4
    handle = 4
    sig = {}
    sig_blocks = []
    for names, sgtp in ((di, None), (ai, "2"), (si, "4"), (do, None), (ao, "2"), (so, "4")):
        for n in names:
            if n in sig:
                continue
            sig[n] = handle
            lines = ["ObjTp=Sg", "H=%d" % handle, "Nm=%s" % n]
            if sgtp:
                lines.append("SgTp=" + sgtp)
            sig_blocks.append(block(lines))
            handle += 1

    ins = di + ai + si
    outs = do + ao + so
    sm = ["ObjTp=Sm", "H=1", "SmC=157", "Nm=Central Control Modules", "ObjVer=1", "CF=2", "n1I=1", "n1O=1",
          "mI=1", "mO=1", "tO=1", "mP=1", "P1="]
    out.append(block(sm))
    out.append(block(["ObjTp=Sm", "H=2", "SmC=156", "Nm=Logic", "ObjVer=1", "CF=2", "mC=1", "C1=4"]))
    out.append(block(["ObjTp=Sm", "H=3", "SmC=157", "Nm=DefineArguments", "ObjVer=1", "CF=2", "n1I=1", "n1O=1",
                      "mI=1", "mO=1", "tO=1", "mP=1", "P1="]))
    usp = ["ObjTp=Sm", "H=4", "SmC=103", "Nm=MiYue Player v1.0.usp", "ObjVer=1", "PrH=2", "CF=2",
           "n1I=%d" % len(di), "n2I=%d" % (len(ai) + len(si)), "n1O=%d" % len(do),
           "mI=%d" % len(ins)]
    usp += ["I%d=%d" % (i + 1, sig[n]) for i, n in enumerate(ins)]
    usp += ["mO=%d" % len(outs), "tO=%d" % len(outs)]
    usp += ["O%d=%d" % (i + 1, sig[n]) for i, n in enumerate(outs)]
    usp += ["mP=%d" % len(params)]
    usp += ["P%d=%s" % (i + 1, PARAM_DEFAULTS.get(p, "")) for i, p in enumerate(params)]
    out.append(block(usp))
    out += sig_blocks

    with open(OUT, "w", encoding="ascii", newline="") as f:
        f.write("".join(out))
    print("wrote %s: %d inputs, %d outputs, %d parameters, %d signals" % (OUT, len(ins), len(outs), len(params), len(sig)))


if __name__ == "__main__":
    main()
