# สถานะการปรับ GameHelper2 ให้ใช้ .NET 10 เต็มรูปแบบ

อัปเดตล่าสุด: 11 กันยายน 2026

เป้าหมายของงานนี้คือทำให้ GameHelper2 ตอบสนองไวขึ้น ลดอาการหน่วง ลดงานซ้ำและลดการสร้างขยะในหน่วยความจำ โดยไม่ได้มุ่งเพิ่ม FPS ของเกมโดยตรง ทุกส่วนจะทำทีละขั้น วัดผล และ commit แยกเพื่อย้อนกลับได้ง่าย

## เสร็จแล้ว

### 1. เปิดระบบปรับโค้ดขณะรันของ .NET 10

- เปิด Tiered Compilation
- เปิด Dynamic PGO เพื่อให้ runtime ปรับ hot path ตามรูปแบบการใช้งานจริง
- เปิด Quick JIT สำหรับช่วงเริ่มต้น
- ปิด Quick JIT สำหรับ loop เพื่อไม่ให้ loop สำคัญติดอยู่กับโค้ดคุณภาพต่ำ
- ตรวจ runtime config แล้วว่าค่าถูกส่งไปยังโปรแกรมจริง

ผลที่คาดหวัง: โปรแกรมตอบสนองดีขึ้นหลังทำงานไประยะหนึ่ง โดยเฉพาะ render loop, การอ่านหน่วยความจำ และการอัปเดต object ที่ถูกเรียกซ้ำบ่อย

Commit: `e82add1 perf: enable tiered PGO and reduce hot-path contention`

### 2. ลด lock และ allocation ในเส้นทาง render/plugin

- สร้าง snapshot รายการปลั๊กอินเฉพาะเมื่อมีการเพิ่ม ลบ หรือล้างรายการ
- render loop ไม่ต้อง lock และสร้าง array ใหม่ทุกเฟรมแล้ว
- เปลี่ยน lock ที่ร้อนเป็น `System.Threading.Lock` ของ .NET รุ่นใหม่
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

ผลที่คาดหวัง: ลดการแย่ง lock, ลด GC pressure และลดช่วงกระตุกจากงานจัดการปลั๊กอิน

Commit: `e82add1 perf: enable tiered PGO and reduce hot-path contention`

### 3. ปรับ Performance Profiler ให้ใช้วัดงานปรับปรุงได้จริง

- ลด allocation ของ profiling scope ใน hot path
- เพิ่มค่า P95 และ P99 เพื่อให้เห็นอาการกระตุกที่ค่าเฉลี่ยซ่อนไว้
- เพิ่มจำนวนหน่วยความจำที่ถูก allocate ต่อการเรียก
- คง API เดิมไว้เพื่อไม่ให้ปลั๊กอินเก่าเสียความเข้ากันได้
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

ผลที่ได้: เราสามารถเทียบก่อนและหลังการแก้ memory reader ด้วย latency ปลายหางและ allocation ไม่ต้องอาศัยความรู้สึก

Commit: `702a8d4 perf: add low-overhead latency and allocation profiling`

### 4. วัดต้นทุนการอ่านหน่วยความจำเกม

เพิ่มตัวเลขในหน้าต่าง Memory Read Diagnostics สำหรับ:

- จำนวน `ReadProcessMemory` ต่อวินาที
- ปริมาณข้อมูลที่ขอต่อวินาที
- เวลาเฉลี่ยต่อ native call
- จำนวน read ที่ล้มเหลว
- สัดส่วน scalar read เทียบกับ buffer/array read

เครื่องมือนี้จะทำงานเมื่อเปิด Memory Read Diagnostics เท่านั้น เพื่อไม่เพิ่มภาระระหว่างเล่นตามปกติ

รายงานสามารถ Copy หรือ Dump ออกมาเปรียบเทียบก่อนและหลังเปลี่ยน memory reader ได้ และตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `85a136e perf: measure native memory read throughput`

Baseline ใน Hideout/UI ก่อนเปลี่ยน wrapper (`memory_diagnostics_20260911_041848.tsv`):

- 6,109,918 native reads ระหว่างช่วงทดสอบ
- 16,283 calls/วินาที ณ เวลาที่ dump
- 8.76 MiB/วินาที
- 1.24 microseconds/native call โดยเฉลี่ย
- 5,123,972 scalar calls และ 985,946 buffer/array calls
- ปริมาณข้อมูลที่ร้องขอรวม 2,256.88 MiB

ตัวเลขนี้ชี้ว่าการลดจำนวน calls จะให้ผลมากกว่าการเปลี่ยนกลไก P/Invoke เพียงอย่างเดียว

### 5.1 ย้าย core memory reader ไป LibraryImport

- สร้าง `NativeProcessMemory` ด้วย source-generated `LibraryImport` ของ .NET 10
- ย้าย OpenProcess, scalar read, array read และ CloseHandle ของ `SafeMemoryHandle`
- ตรวจ byte count ทุกครั้ง ป้องกัน short read ถูกนับเป็นผลสำเร็จ
- เปลี่ยนการหาขนาด unmanaged type จาก `Marshal.SizeOf<T>()` เป็น `Unsafe.SizeOf<T>()` ใน hot path
- self-test อ่านค่า 32-bit จากหน่วยความจำของโปรเซสทดสอบสำเร็จและได้ครบ 4 ไบต์
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error
- ยังเก็บ package เก่าไว้ชั่วคราว เพราะ Atlas2 มีการเรียกโดยตรงและต้องย้ายแยก

Commit: `b4c9a2c perf: migrate core memory reads to LibraryImport`

Runtime validation หลังย้าย core reader (`memory_diagnostics_20260911_042754.tsv`):

- อ่านหน่วยความจำต่อเนื่องสำเร็จ 7,551,461 calls
- total failed reads ลดจาก 392,874 ในรอบแรกเหลือ 6 ในรอบตรวจนี้
- ช่วงเวลาขณะกด dump มีภาระ 336,694 calls/วินาที และ 133.04 MiB/วินาที จึงไม่ควรนำเวลา 1.91 microseconds/call ไปเทียบตรง ๆ กับ snapshot รอบแรกที่มีเพียง 16,283 calls/วินาที
- สรุปได้ว่า runtime correctness ผ่าน แต่ยังไม่สรุปว่าตัว interop ใหม่เร็วกว่าโดยอาศัย snapshot ต่างภาระ

### 5.2 ย้าย Atlas2 และถอด ProcessMemoryUtilities.Net

- ลบ process handle แยกของ Atlas2 และใช้ `Core.Process.Handle` ร่วมกับโปรแกรมหลัก
- เพิ่ม bulk-read API ที่อ่านลง buffer ของผู้เรียกโดยไม่สร้างสำเนาอีกชุด
- Atlas2 ใช้ validation, short-read detection และ diagnostics กลางเหมือนส่วนอื่น
- ถอด package reference `ProcessMemoryUtilities.Net 1.3.4`
- restore ยืนยันว่า package หายจาก dependency graph
- ตรวจ output แล้วไม่มี `ProcessMemoryUtilities.dll` เก่าค้าง
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `941cee1 refactor: remove legacy process memory package`

### 5.3 เพิ่มค่าเฉลี่ย memory reads ทั้ง session

- แยกตัวเลข `Recent` ช่วง 0.5 วินาทีล่าสุดออกจาก `Session`
- Session แสดงระยะเวลา, calls/วินาที, MiB/วินาที, เวลาเฉลี่ย/call และ failures รวม
- ทำให้ dump จากภาระต่างช่วงเปรียบเทียบกันได้แม่นยำขึ้น
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `5a967fa perf: add session-wide memory read metrics`

### 5.4 แก้การ Dump เมื่อไม่มี failed reads

- เดิมปุ่ม Copy/Dump ไม่สร้างรายงานเมื่อ failure table ว่าง แม้ Session metrics มีข้อมูล
- แก้ให้บันทึกรายงานได้เมื่อมี successful reads และ failures เป็นศูนย์
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `df168aa fix: allow zero-failure memory metric dumps`

### 5.5 ตรวจ Atlas2 หลังถอด wrapper และแก้ frame limit ตอนเริ่มโปรแกรม

ผลทดสอบ Atlas2-only (`memory_diagnostics_20260911_044050.tsv`):

- Session 67.0 วินาที อ่านสำเร็จ 25,820,661 calls
- เฉลี่ย 385,151 calls/วินาที และ 145.28 MiB/วินาที
- เวลา native call เฉลี่ยทั้ง session 1.76 microseconds
- native failures เป็นศูนย์ และพบ null address validation เพียง 1 ครั้ง
- ยืนยันว่า LibraryImport, shared process handle และ Atlas2 ทำงานร่วมกันได้

ตอนแรกสงสัยว่าค่า `FPSLimit = 60` ถูกโหลดจาก settings แต่ไม่ได้ส่งให้ overlay ตอนเริ่มโปรแกรม จึงแก้ให้ค่าที่ผู้ใช้บันทึกถูกนำไปใช้เสมอ

- แก้ `GameOverlay.Run()` ให้ใช้ FPS limit ที่บันทึกไว้ตั้งแต่เริ่ม
- การจำกัดนี้มีผลเฉพาะ GameHelper overlay ไม่ได้จำกัด FPS ของเกม
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `7e2956f perf: apply overlay frame limit at startup`

ผลทดสอบถัดมา (`memory_diagnostics_20260911_044408.tsv`) พบว่า read rate ยังสูงถึง 550,153 calls/วินาที จึงตรวจ source ของ ClickableTransparentOverlay 11.1.0 เพิ่มและพบว่าตัวไลบรารีตั้งค่าเริ่มต้นไว้ที่ 60 FPS อยู่แล้ว สรุปว่า frame-limit fix ทำให้ custom setting ถูกต้อง แต่ไม่ใช่คำอธิบายหลักของ read rate ที่สูง

### 5.6 เพิ่ม reads/frame และ read-region attribution

- เพิ่มจำนวน frame, frames/วินาที และ reads/frame ในรายงาน
- แก้ให้ `PerformanceProfiler.EndFrame()` ถูกเรียกจริงหลังจบรอบ render
- เพิ่ม scope วัดจำนวน read แยกระหว่าง `Core.AtlasMapUpdate` และ `Atlas2.DrawUI`
- scope เก็บผลเฉพาะตอนเปิด diagnostics และไม่ stack-walk ทุก read
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `643a2e6 perf: report memory reads per overlay frame`

Commit: `8de6e70 perf: attribute memory reads to Atlas regions`

ผล baseline แบบ reads/frame (`memory_diagnostics_20260911_044950.tsv`):

- Session 30.2 วินาที รวม 21,187,544 reads
- เฉลี่ย 700,611 reads/วินาที, 255.04 MiB/วินาที และ 1.73 microseconds/call
- overlay ทำงานเฉลี่ย 57.0 FPS หรือ 12,297 reads/frame
- `Atlas2.DrawUI`: เฉลี่ย 2,431 reads ต่อการเรียก
- `Core.AtlasMapUpdate`: เฉลี่ย 1,370 reads ต่อการเรียก
- เหลือประมาณ 8,496 reads/frame จากระบบ UI/entity อื่น
- native failures เป็นศูนย์

ตัวเลขนี้ยืนยันว่าจำนวนการเรียก Windows สูงถึงหลักแสนต่อวินาทีจริง และคอขวดที่ควรแก้ก่อนคือ read ซ้ำต่อเฟรม ไม่ใช่ marshaller หรือ FPS limit

### 5.7 ทดลองเก็บ Atlas node cache ข้ามเฟรม (ยกเลิกแล้ว)

- พบว่า Atlas panel อ่านรายชื่อ child แบบ batch อยู่แล้ว แต่ล้าง object cache ของลูกประมาณ 751 โหนดทุกเฟรม
- Atlas2 จึงต้องสร้าง `UiElementBase` และอ่านข้อมูล node เดิมซ้ำเมื่อคำนวณตำแหน่งวาด
- เพิ่มโหมดเก็บ cached child เฉพาะ container ขนาดใหญ่ที่ข้อมูลค่อนข้างคงที่ และเปิดใช้เฉพาะ Atlas เพื่อลดผลกระทบต่อ UI อื่น
- ถ้า child address ที่ index เดิมเปลี่ยน cache จะถูกทิ้งทันทีเพื่อรักษาความถูกต้อง
- refresh node จริงยังทำทุก 20 เฟรมตามรอบ Atlas data cache เดิม จึงไม่ลดความสดของข้อมูล topology/status จากพฤติกรรมเดิม
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error
- รอ runtime dump รอบถัดไปเพื่อเทียบ `reads/frame` และ `Atlas2.DrawUI reads/invocation` กับ baseline ข้างต้น

Commit: `7fd6936 perf: preserve stable Atlas node cache`

Runtime validation หลังเก็บ cache (`memory_diagnostics_20260911_045604.tsv`):

- `Atlas2.DrawUI` ลดจาก 2,431.4 เหลือ 91.3 reads/ครั้ง หรือลดลง 96.2%
- ทั้งโปรแกรมลดจาก 12,297 เหลือ 11,660 reads/frame หรือลดลง 5.2%
- ค่าเฉลี่ยทั้ง session ลดจาก 700,611 เหลือ 660,789 reads/วินาที
- buffer/array calls ลดจาก 7,195,958 เหลือ 6,107,064 ในช่วงทดสอบประมาณ 30 วินาที
- native failures ยังเป็นศูนย์

ผลนี้ยืนยันว่า cache ทำงานจริงและ Atlas2 ไม่ใช่แหล่ง read หลักอีกต่อไป ส่วนตัวเลข region เดิมใช้ผลต่างของ counter รวมทั้งโปรเซส จึงมี read จากงานขนานปะปนและยังใช้ชี้คอขวดที่เหลือไม่ได้แม่นยำ

Runtime visual validation พบว่าตัวหนังสือ Atlas2 ตาม node ไม่ทันระหว่างเลื่อนแผนที่ เพราะ relative position ของ node ถูก refresh ตามรอบ cache 20 เฟรมแทนทุกเฟรม จึงยกเลิก optimization นี้ทั้งหมดและคืนการอัปเดตตำแหน่งทุกเฟรม ความถูกต้องและ responsiveness สำคัญกว่าตัวเลข read ที่ลดลง

Rollback commit: `8d16125 Revert "perf: preserve stable Atlas node cache"`

### 5.8 แยก memory read ตาม logical operation และ thread

- เปลี่ยน read-region attribution ให้ใช้ context ของงานซึ่งไหลต่อไปยัง `Task`/งานขนาน
- แต่ละ native read ถูกนับเข้า operation ที่เป็นเจ้าของโดยตรง แทนการหัก counter รวมก่อนและหลัง operation
- เพิ่ม region ของ GameStates, InGameState, AreaInstance, ImportantUiElements, Inventory, ServerData, WorldData และ plugin แต่ละตัว
- bookkeeping นี้ทำงานเฉพาะเมื่อเปิด Memory Read Diagnostics
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error
- รอ dump รอบถัดไปเพื่อจัดอันดับแหล่งของ read ที่เหลือประมาณ 11,660 reads/frame แล้วจึงทำ batch/cache จุดที่คุ้มที่สุด

Commit: `d14c1de perf: attribute memory reads by logical operation`

ผลจากตัววัดใหม่ (`memory_diagnostics_20260911_050050.tsv`):

- ทั้งโปรแกรม 11,656 reads/frame, 661,811 reads/วินาที และไม่มี failure
- `Core.ImportantUiElements`: 11,440 reads/ครั้ง หรือประมาณ 98.1% ของทั้งเฟรม
- `Core.AtlasMapUpdate`: 1,478 reads/ครั้ง ซึ่งเป็นส่วนย่อยของ ImportantUiElements
- `Core.AreaInstance`: 125 reads/ครั้ง
- `Atlas2`: 91 reads/ครั้งในช่วงที่ทดลองใช้ cache
- Inventory, InGameState, GameStates และ WorldData รวมกันมีสัดส่วนน้อยมาก

เป้าหมายถัดไปจึงเป็นการลด read ซ้ำภายใน parent/UI-path refresh โดยต้องรักษาการ refresh ตำแหน่ง Atlas node ทุกเฟรม ไม่ใช้ cache ตำแหน่งแบบรอบละ 20 เฟรมอีก

### 5.9 ลด read ซ้ำของ cached UI parent โดยไม่ลดความสดของตำแหน่ง

- พบว่า cached UI parent ถูกอ่านโครงสร้างหนึ่งรอบเพื่อ validate แล้วถูกอ่านซ้ำอีกครั้งผ่าน `Address` setter
- การ refresh ปกติยังอ่าน child vector ของทุก parent แม้ผู้ใช้ cache นี้ต้องการเพียง position, scale, flags และ parent pointer เพื่อวาด/คำนวณตำแหน่ง
- ให้ `ImportantUiElements` ใช้ snapshot เดียวทั้ง validate และ refresh ฟิลด์ที่จำเป็น โดยไม่อ่าน child vector ของ parent cache
- Atlas panel และ Atlas node หลักยัง refresh ตาม path ปกติทุกเฟรม จึงต้องไม่มีอาการ text ตาม node ไม่ทันแบบ optimization ก่อนหน้า
- เพิ่ม region `Core.UiElementParents` เพื่อยืนยันผลของจุดนี้ใน dump รอบถัดไป
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `7f76145 perf: avoid redundant cached parent UI reads`

Runtime validation (`memory_diagnostics_20260911_051517.tsv`):

- ทั้งโปรแกรมลดจาก 11,656 เหลือ 4,111 reads/frame หรือลด 64.7%
- read rate ลดจาก 661,811 เหลือ 246,286 reads/วินาที
- overlay จาก 56.8 เป็น 59.9 FPS
- native failures เป็นศูนย์
- `Core.UiElementParents` ยังใช้ 1,647 reads/frame และ Atlas2 ใช้ 1,476 reads/frame เพราะตำแหน่ง node ต้องสดทุกเฟรม

### 5.10 Refresh Atlas node สดทุกเฟรมโดยไม่อ่าน child vector ซ้ำ

- ยกเลิกแนวทาง cache ข้อมูล node นาน 20 เฟรมแล้ว เพราะทำให้ label ตามโหนดไม่ทัน
- เก็บ object cache เฉพาะ child ที่ address เดิมเท่านั้น และ refresh position/scale/flags ของ node ที่ materialize แล้วทุกเฟรม
- ไม่อ่าน child vector ของ node ระหว่าง refresh เพราะ Atlas2 ใช้เฉพาะข้อมูลตำแหน่งและ visibility
- เมื่อ address หรือ index เปลี่ยน cache slot จะถูกทิ้งและสร้างใหม่ตามปกติ
- เป้าหมายคือรักษาการเคลื่อนไหว label ให้ตรง node แบบเดิม พร้อมลด read ของ Atlas2 จาก struct + child vector เหลือ struct เดียวต่อ node
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `4e46ee9 perf: refresh Atlas nodes without child vector reads`

Runtime validation (`memory_diagnostics_20260911_051957.tsv`):

- ทั้งโปรแกรมลดเหลือ 3,849 reads/frame หรือ 229,726 reads/วินาที
- เทียบ baseline ก่อนปรับ UI parent ที่ 11,656 reads/frame ลดลงรวม 67.0%
- `Atlas2.DrawUI` ลดจาก 1,475.8 เหลือ 61.0 reads/ครั้ง หรือลด 95.9%
- Atlas2 ยังคง refresh position/size/visibility ของ node ที่ใช้งานทุกเฟรม; การลดลงมาจากการไม่อ่าน child vector ที่ไม่ใช้
- native failures เป็นศูนย์ และ overlay อยู่ที่ 59.7 FPS

ผลหลักของเฟสนี้คือทำให้ overlay รอ Windows/kernel เพื่ออ่าน memory น้อยลง จึงลดโอกาสหน่วงและทำให้ UI ตอบสนองสม่ำเสมอขึ้น ไม่ได้ตั้งใจเพิ่ม FPS ของตัวเกม

## กำลังทำ

เฟส 5 — ลดจำนวน native memory calls และ allocation โดยใช้ baseline ที่เก็บไว้ชี้จุดคุ้มที่สุด

## ลำดับถัดไป

### 5. ลดต้นทุนของ memory reader ต่อ

การย้าย wrapper และถอด package เสร็จแล้ว งานที่เหลือในเฟส memory reader คือ:

1. ~~เพิ่มค่าเฉลี่ยทั้ง session แยกจาก snapshot ณ เวลาที่กด Dump~~
2. ลด allocation ของ array/string reads และใช้ buffer ซ้ำในจุดที่ปลอดภัย
3. รวม read ที่อยู่ติดกันเพื่อลดจำนวนการข้ามจาก .NET ไป Windows
4. หา call site ที่เรียกถี่ผิดปกติ แล้วเพิ่ม cache/throttle โดยไม่ทำให้ข้อมูลสำคัญล่าช้า

ประโยชน์หลักจะมาจากการลดจำนวน native calls และ allocation ไม่ใช่เพียงการเปลี่ยนชื่อ wrapper

### 6. JSON และ API เก่า

- ค่อย ๆ ย้ายงาน JSON ที่เป็น hot path หรือมีโครงสร้างแน่นอนไป `System.Text.Json` พร้อม source generation
- เก็บ Newtonsoft.Json ไว้ชั่วคราวในจุดที่ต้องใช้ polymorphism หรือ `TypeNameHandling` จนกว่าจะมีตัวแทนที่ปลอดภัย
- ทยอยเปลี่ยน `DllImport` ที่เหมาะสมเป็น `LibraryImport`
- ตรวจ package เก่าและ API ที่เลิกแนะนำทีละกลุ่ม

## การตัดสินใจเรื่อง Native AOT

ยังไม่เปิด Native AOT ให้ GameHelper ตัวหลัก

เหตุผลคือระบบปลั๊กอินปัจจุบันโหลด DLL ภายนอกตอนรันด้วย `AssemblyLoadContext`, ค้นหา type ด้วย reflection และสร้างปลั๊กอินแบบ dynamic ขณะที่ Native AOT ไม่รองรับ dynamic assembly loading การเปิดทันทีจะทำให้ระบบปลั๊กอินหลักใช้งานไม่ได้

แนวทางที่เหมาะกว่า:

- ใช้ Tiered PGO ต่อไปสำหรับประสิทธิภาพระยะยาว
- ทดสอบ ReadyToRun สำหรับลดเวลาเปิดและ first-use latency โดยยังรักษาระบบปลั๊กอิน
- ใช้ AOT compatibility analyzer เพื่อลด reflection ที่ไม่จำเป็นและเตรียมโค้ดให้ดีขึ้น แม้ยังไม่ publish แบบ Native AOT
- พิจารณา Native AOT เฉพาะ Launcher หรือเครื่องมือย่อยที่ไม่โหลดปลั๊กอิน หากผลวัดแสดงว่าคุ้มค่า

เอกสารอ้างอิง: [Native AOT deployment overview](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) และ [.NET application publishing overview](https://learn.microsoft.com/dotnet/core/deploying/)

## กฎการทำงาน

- ทำทีละส่วนที่ให้ผลคุ้มที่สุดก่อน
- วัดก่อนและหลังทุกงานที่เกี่ยวกับ performance
- Release build ต้องผ่านก่อน commit
- commit แยกตามงาน
- ไม่รวมไฟล์ส่วนตัวหรือการเปลี่ยนแปลงที่ไม่เกี่ยวข้องใน commit
