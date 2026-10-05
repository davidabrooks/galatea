#!/usr/bin/env python3
"""check: `look` meshes the near scene only (30 m, no backdrop group) unless --far. run: imgvenv python vision/test_look.py"""
import os, sys; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import look
me = [1, 2, 3]
assert look.mesher_args("d", "around", me, False) == ["d", "12", "all", "1,2,3", "30", "--roots=32"]
assert look.mesher_args("d", "view", me, True)[-2:] == ["96", "--far=30"]
assert look.mesher_args("d", "self", me, True) == ["d", "12", "avatar"]
print("test_look ok")
