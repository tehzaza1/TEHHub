# Project Rules & Guidelines

## 1. Mandatory Git Commit Policy
- **สำคัญมาก (Critical)**: ทุกครั้งที่มีการแก้ไขโค้ด ปรับปรุงฟีเจอร์ หรือแก้บักเสร็จเรียบร้อย **ต้องทำ `git commit` ทันทีเสมอ**
- ห้ามปล่อยให้โค้ดที่ทำงานได้แล้วค้างอยู่ในสถานะ Uncommitted เด็ดขาด
- บันทึก Commit Message ให้ชัดเจน เป็นระเบียบ เพื่อเก็บประวัติการทำงานทุกขั้นตอน

## 2. Deployment Policy
- เมื่อคอมไพล์ `GameOverlay.sln -c Release` สำเร็จด้วย 0 Errors/0 Warnings ให้รัน `deploy.py` เพื่อซิงค์ไฟล์ไปยัง `C:\Games\Hy-v Tool\DXPEOE\GameHelper2\`

## 3. Patch-Day Heuristic Offset Discovery (สูตรงมหา Offset สดๆ วันเปิดลีคใหม่)
เมื่อเกมอัปเดตลีคใหม่แล้ว Offset หรือ Signature หลุด ให้ใช้เทคนิค **Heuristic Memory Probing** ค้นหาตำแหน่งสดๆ จาก Memory ดังนี้:

### Step 1: Base Pattern Recovery (AOB Signature)
- สแกนหา String อ้างอิงที่ไม่เคยเปลี่ยนในเนื้อไฟล์ `.exe` หรือ Module Memory:
  - `"Unable to get InGameState"` ➔ คำนวณ RIP-relative displacement เพื่อหา Base ของ **Game States**
  - `"Mods.dat"` ➔ Base ของ **File Root**
  - `"Got Instance Details from login server"` ➔ **AreaChangeCounter**
- นำ Opcode Assembly รอบๆ จุดเรียกมาทำเป็น AOB Pattern ใส่ใน `GameOffsets/StaticOffsetsPatterns.cs`

### Step 2: Heuristic AreaInstance & Player Chasing (ไม่ต้องรู้ Offset มาก่อน)
- จาก `InGameState` สแกนหา Pointer ทุกตัวในย่าน `0x000` - `0x500` (ก้าวทีละ 8 ไบต์)
- ภายในแต่ละ Pointer ที่ถูกต้อง ให้ตรวจดู Pointer ย่อย `0x000` - `0x800` ว่าตัวไหนชี้ไปยัง Entity ที่มี `Details->name` ขึ้นต้นด้วย `"Metadata/Characters/"`:
  - Pointer ย่อยที่เจอ ➔ คือ **LocalPlayerPtr** (เช่น `0x5D0`)
  - ตำแหน่งใน InGameState ที่เก็บ Pointer นี้ ➔ คือ **AreaInstanceData** (เช่น `0x290`)

### Step 3: Heuristic Vital Pools (HP / MP / ES Discovery)
- ไปที่ Component `"Life"` ของ Player
- สแกนหาคู่ข้อมูลจำนวนเต็ม 4 ไบต์ `(Total, Current)` ที่อยู่ในเงื่อนไข `0 < Current <= Total <= 50000`:
  - พบคู่แรก ➔ **Health (HP)** (เช่น `0x1DC` / `0x1E0` ใน `VitalStruct Health`)
  - พบคู่ถัดไป ➔ **Mana (MP)** (เช่น `0x234`)
  - พบคู่ถัดไป ➔ **Energy Shield (ES)** (เช่น `0x274`)

### Step 4: Area Level Discovery
- ภายใน `AreaInstance` สแกนหาตัวแปร 1 ไบต์ (Byte) ที่มีค่าตรงกับเลเวลของด่านที่ตัวละครยืนอยู่ (1 - 100) ➔ ได้ออฟเซ็ตของ **CurrentAreaLevel** (เช่น `0x0BC`)

