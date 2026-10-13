# Open items

- 2026-10-12 renderer (vision/look.py, scene-mesher): the unrigged 'Kay HP' lens discs (Skull attachment, rotated
  0,-0.70,0,0.71) render off-centre in the headphone rings; David's viewer shows them centred. Likely the attachment
  rotation/offset of unrigged Skull attachments on a Kay/EvoX avatar in the mesher.
- 2026-10-12 `attach move` only changes the rezzed attachment; the inventory item keeps the new position after a
  detach (or logout). Re-wearing an outfit while it is attached re-rezzes the old item. Detach + re-attach after a move.
