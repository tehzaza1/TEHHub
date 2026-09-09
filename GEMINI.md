# Project Rules & Guidelines

## 1. Mandatory Git Commit Policy
- **สำคัญมาก (Critical)**: ทุกครั้งที่มีการแก้ไขโค้ด ปรับปรุงฟีเจอร์ หรือแก้บักเสร็จเรียบร้อย **ต้องทำ `git commit` ทันทีเสมอ**
- ห้ามปล่อยให้โค้ดที่ทำงานได้แล้วค้างอยู่ในสถานะ Uncommitted เด็ดขาด
- บันทึก Commit Message ให้ชัดเจน เป็นระเบียบ เพื่อเก็บประวัติการทำงานทุกขั้นตอน

## 2. Deployment Policy
- เมื่อคอมไพล์ `GameOverlay.sln -c Release` สำเร็จด้วย 0 Errors/0 Warnings ให้รัน `deploy.py` เพื่อซิงค์ไฟล์ไปยัง `C:\Games\Hy-v Tool\DXPEOE\GameHelper2\`
