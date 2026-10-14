#!/bin/bash
# Cancel a running photo/look: SIGTERM look.py (its handler SIGKILLs each render child's process group; children also
# die with it via PR_SET_PDEATHSIG), then sweep any orphan render_mesh.py Blender / scene-mesher left by older runs.
pkill -TERM -f "vision/look.py" ; sleep 2
pkill -KILL -f "cloud-render/render_mesh.py" ; pkill -KILL -f "SceneMesher" ; pkill -KILL -f "scene-mesher"
pgrep -fa "look.py|render_mesh.py|SceneMesher" || echo "no render processes left"
