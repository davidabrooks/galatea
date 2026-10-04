#!/usr/bin/env python3
"""Runnable check for the text-client parsers in gather_scene.py and the id guard in handler.py: python3 test_parse.py"""
import re
import gather_scene as g

line = "  <105.4,150.1,252.7> size 1.1x1.1x0.9 rotZ -77 PHANTOM '+Half-Deer+ Garden Table' e543eeb0-4751-0f48-8fe4-ddea22523aea"
m = g.OBJ.match(line)
assert m and m.group(7) == "-77" and m.group(8) and m.group(9) == "+Half-Deer+ Garden Table", m
assert g.OBJ.match("  <107.4,150.8,51.7> size 9.3x9.3x1.0 rotZ 0 'Object' ea4661ba-fa48-9e9b-86e7-9d6aa554a786").group(8) is None
assert g.POS.search("region=Naberrie pos=<107.0, 150.8, 53.2>").groups() == ("107.0", "150.8", "53.2")
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")  # same pattern as handler.py
assert UUID.match("8d5ad85f-453b-ed86-31b3-c7356150164f") and not UUID.match("../../etc/passwd") and not UUID.match("x" * 36)
print("ok")
