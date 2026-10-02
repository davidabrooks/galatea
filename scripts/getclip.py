#!/usr/bin/env python3
# Prints the X CLIPBOARD text of the current DISPLAY (used to read object keys copied in Firestorm).
import sys, time
from Xlib import X, display, Xatom
d = display.Display(); w = d.screen().root.create_window(0,0,1,1,0,X.CopyFromParent)
CLIP = d.intern_atom('CLIPBOARD'); UTF8 = d.intern_atom('UTF8_STRING'); PROP = d.intern_atom('XSEL_DATA')
w.convert_selection(CLIP, UTF8, PROP, X.CurrentTime); d.flush()
end = time.time()+3
while time.time() < end:
    if d.pending_events():
        e = d.next_event()
        if e.type == X.SelectionNotify:
            if e.property == X.NONE: print("(clipboard empty)"); sys.exit(1)
            print(w.get_full_property(PROP, X.AnyPropertyType).value.decode('utf-8','replace')); sys.exit(0)
    else: time.sleep(0.05)
print("(timeout)"); sys.exit(2)
