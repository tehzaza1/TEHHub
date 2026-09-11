# Local Diagnostics API

GameHelper เปิด API สำหรับตรวจ offset ภายในเครื่องที่ `http://localhost:9877` เท่านั้น
API นี้ไม่รับคำสั่งควบคุมตัวละคร ไม่สั่งปลั๊กอิน และไม่เปิดรับจากเครื่องอื่น

## ใช้งาน

1. เปิดเกมและ GameHelper ตามปกติ
2. เข้าเกมจนตัวละครอยู่ในพื้นที่เล่นได้
3. GameHelper จะตรวจ offset หนึ่งครั้งเองหลังข้อมูลนิ่งประมาณ 2 วินาที

ตรวจสถานะปัจจุบัน:

```powershell
curl.exe http://localhost:9877/api/diagnostics/offset-status
```

สั่งตรวจซ้ำหลังเกมอัปเดตหรือหลังเปลี่ยนพื้นที่:

```powershell
curl.exe -X POST -d "" http://localhost:9877/api/diagnostics/offset-verify
```

เรียกสถานะอีกครั้งจน `selfTestRunning` เป็น `false`:

```json
{
  "selfTestRunning": false,
  "verificationQueued": false,
  "summary": "rescan PASS — 12 unchanged, 0 relocated"
}
```

## ความหมายของผล

- `rescan PASS`: signature/static address ที่ GameHelper ใช้อ่านยังหาเจอ
- `relocated`: signature ยังถูกต้อง แต่ตำแหน่งใน process เปลี่ยน ซึ่งระบบ address resolver จัดการได้
- `rescan FAILED`: pattern สำคัญหาไม่เจอ ต้องตรวจ offset ก่อนเชื่อผลจากฟีเจอร์ที่เกี่ยวข้อง

การตรวจนี้ยืนยัน static pattern และ sanity-check ของ struct ที่มีข้อมูลสดอยู่แล้ว ไม่ได้เดา offset ใหม่ทุก field โดยอัตโนมัติเมื่อ game patch เปลี่ยน layout อย่างมีนัยสำคัญ
