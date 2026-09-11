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

### 5.11 Batch reading สำหรับ cached Root UI parents

- Dump ยืนยันว่า Root UI parent ใช้ 1,989 reads/frame ขณะที่ Passive Tree ใช้ 0 ในสถานะทดสอบ
- เพิ่ม Batch Reading สำหรับ parent ที่ address อยู่ติดกันใน memory: อ่าน span เดียวแล้วแยก `UiElementBaseOffset` ใน GameHelper
- จำกัด gap และขนาดก้อนสูงสุดเพื่อไม่อ่านข้าม heap region ขนาดใหญ่
- หาก Windows อ่านก้อนไม่ครบ จะ fallback ไปอ่าน parent แต่ละตัวด้วยกลไกเดิมทันที จึงยังได้ข้อมูล UI ชุดเดิม
- ใช้ `ArrayPool<byte>` และ overload read ที่ระบุความยาวจริง เพื่อไม่สร้าง allocation ใหญ่ทุกเฟรมและไม่อ่านพื้นที่เกินก้อนที่ต้องการ
- การเปลี่ยนนี้ไม่ลด update rate ของตำแหน่ง, visibility หรือ scale
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error
- รอ runtime/visual validation เพื่อวัดผล calls/frame และยืนยันว่าไม่มี fallback/failure ผิดปกติ

Commit: `5ca1df1 perf: batch adjacent UI parent memory reads`

Runtime validation (`memory_diagnostics_20260911_052900.tsv`):

- ทั้งโปรแกรมลดจาก 3,908 เหลือ 2,947 reads/frame หรือลด 24.6%
- Root UI parent ลดจาก 1,989 เหลือ 1,188 reads/frame หรือลด 40.3%
- read rate เฉลี่ยลดจาก 236,863 เหลือ 179,475 reads/วินาที
- native failures เป็นศูนย์ และไม่พบ batch fallback ผิดปกติ
- requested bytes เพิ่มจาก 118.28 เป็น 130.49 MiB/วินาที เพราะ span ครอบช่องว่างระหว่าง allocation ที่ติดกัน แต่ยังคุ้มค่าจากการลดการข้าม Windows/kernel
- overlay อยู่ที่ 60.9 FPS ระหว่างทดสอบ และผู้ใช้ตรวจ Atlas/minimap/panel แล้ว

### 5.12 ทดลองรวม Atlas node header fields เป็นหนึ่ง native read (ยกเลิกแล้ว)

- ตอน refresh Atlas topology เดิมอ่าน flags, node-data pointer และ grid position แยกกันในแต่ละ node
- เพิ่ม `AtlasNodeUiHeader` แบบ explicit layout เพื่ออ่าน field เหล่านี้ (รวม region-button fields) ครั้งเดียวต่อ node
- ใช้ค่าเดิมในการจำแนก marker/map/ship และตาม pointer chain เดิมทุกจุด
- ลดจำนวน kernel calls โดยแลกกับการอ่าน header ที่กว้างขึ้น ซึ่งปลอดภัยเพราะอยู่ภายใน allocation ของ node UI เดียวกัน
- ไม่ลดรอบ refresh 20 เฟรมของ topology และไม่ลดการ refresh ตำแหน่งทุกเฟรม
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error
- Runtime validation (`memory_diagnostics_20260911_054324.tsv`) ไม่มี failure แต่
  `Core.AtlasMapUpdate` อยู่ที่ 1,501 reads/ครั้ง เทียบกับ 1,432 ก่อนหน้า จึงไม่พบผลลด calls ที่มีนัยสำคัญภายใต้ workload นี้
- เนื่องจาก optimization นี้เพิ่มขนาด read ต่อ node แต่ไม่ให้ผลที่วัดได้ชัด จึง revert เพื่อคงโค้ด Atlas ที่เรียบง่ายและเสี่ยงต่ำกว่า

Commit: `caa0424 perf: combine Atlas node header reads`

Rollback commit: `c8b2203 Revert "perf: combine Atlas node header reads"`

### 5.13 ลด allocation จาก fixed-size memory buffers

- การอ่าน ASCII/UTF-16 ที่ปลายทางยังคงขนาดและจำนวน native reads เดิม แต่เช่า buffer จาก `ArrayPool<byte>` แทนการสร้าง byte array ใหม่ทุกครั้ง
- Terrain height refresh อ่านลง array เดิมของ object โดยตรง; หากอ่านไม่สำเร็จจะล้างค่าเป็นศูนย์เหมือนผลลัพธ์ปลอดภัยเดิม
- Minimap icon ใช้ buffer ที่เช่าจาก pool ขนาด 512 bytes และคืนทุกกรณี รวมถึงกรณี read หรือ parse ล้มเหลว
- ไม่เปลี่ยนการ refresh, cache หรือข้อมูลที่นำไปวาด จึงไม่ทำให้ Atlas/minimap ตามข้อมูลช้าลง

### 5.14 แยก profiler ของ Entity เพื่อหาจุด GC ที่แท้จริง

- `Entity.UpdateData` เป็นค่า inclusive ที่รวมงานของ component ย่อย จึงแยกตัววัดของการ refresh map, refresh component ที่ cache, classify และ calculate state
- instrumentation ทำงานเฉพาะขณะเปิด Performance Profiler และไม่เปลี่ยน memory reads, refresh rate หรือการจัดประเภท entity
- ผลรอบถัดไปจะใช้เลือกลด allocation จากจุดที่วัดได้จริง แทนการเดาและเสี่ยงทำให้ Radar/ปลั๊กอินอ่านข้อมูลช้า

### 5.15 แยก profiler ภายใน Radar.DrawUI

- แยกวัด `CollectEntityPaths`, `RebuildEntityPaths`, `RebuildTrackedNodes` และแต่ละ draw pass ของ large map/minimap
- ยังไม่เปลี่ยน logic หรือจำนวนรอบ refresh; ใช้เพื่อระบุแหล่ง allocation ของ Radar ประมาณ 50 KiB/เฟรมจาก runtime dump

### 5.16 ปรับ Performance Profiler ให้ดูค่าเฉลี่ยสะสมเป็นค่าเริ่มต้น

- เปลี่ยนค่าเริ่มต้นจาก `Current Frame Only` เป็นค่าเฉลี่ยสะสมตลอด session เพื่อไม่ให้ผลสะท้อนเฉพาะ call ล่าสุด
- ยังเลือก `Current Frame Only` ได้จาก checkbox เมื่อจำเป็นต้องตรวจเฟรมเดียว
- เปลี่ยนการเรียงลำดับเริ่มต้นเป็น `Alloc (Call)` จากมากไปน้อย เพื่อให้เห็นคอขวด allocation ในภาพเดียว
- `P95/P99` ยังคงใช้หน้าต่างตัวอย่างล่าสุด 100 ค่า เพื่อจับ latency spike โดยไม่เก็บข้อมูลไม่จำกัด

### 5.17 Cache ผล clustering ของ Radar ต่อแผนที่

- `DrawTgtFiles` และ `DrawDirectionLines` ไม่คำนวณ `ClusterTileLocations` ซ้ำทุกเฟรมสำหรับ POI เดิม
- เก็บผล cluster ต่อ pattern ใน `poiClusterCache` และล้างเมื่อเปลี่ยน area instance
- ไม่เปลี่ยนตำแหน่ง, ลำดับ, การกรอง label หรือ pathfinding ของ POI; เป็นการ reuse ผลคำนวณเท่านั้น
- ตรวจ Release build ของ Radar ผ่าน 0 warning / 0 error; ต้องวัด runtime dump รอบถัดไปก่อนสรุปผลจริง

### 5.18 Cache door override map ของ Radar

- `DrawDirectionLines` และงาน pathfinding ใช้ door override map เดียวกันตลอด area แทนการสร้าง `HashSet` ใหม่ทุกเฟรม
- cache จะสร้างใหม่เมื่อเปลี่ยน area หรือจำนวน `AwakeEntities` เปลี่ยน เพื่อรองรับ entity/ประตูที่เพิ่งโหลดเข้ามา
- ไม่เปลี่ยนกฎการเดินเส้นทาง และยังส่ง map เดิมให้ `LineWalker`/pathfinder
- ตรวจ Release build ของ Radar ผ่าน 0 warning / 0 error; ต้องวัด runtime dump เพื่อยืนยัน headroom จริง

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

### 6.1 ย้าย localization JSON ไป System.Text.Json

- `PluginLocalization` และ `OverlayLocalization` อ่านไฟล์ localization ที่มี schema ตายตัวเป็น `Dictionary<string, string>`
- ย้ายสองจุดนี้จาก Newtonsoft ไป `System.Text.Json` ที่มากับ .NET 10 แล้ว
- ใช้ options เดียวที่รองรับชื่อ property ต่างตัวพิมพ์เพื่อคง tolerance เดิมของไฟล์ภาษา
- ยังไม่ถอด package Newtonsoft จาก GameHelper เพราะยังมี JSON DOM (`JObject`) และ enum converter ในส่วนอื่นที่ต้องย้ายแยกอย่างปลอดภัย
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `a5a1bed refactor: use System.Text.Json for localization`

### 6.2 ย้าย P/Invoke ที่เหลือของ Atlas2

- ย้าย `GetForegroundWindow` จาก `DllImport` เป็น source-generated `LibraryImport`
- เปิด `AllowUnsafeBlocks` เฉพาะโปรเจกต์ Atlas2 ตามข้อกำหนดของ LibraryImport generator
- ตรวจ source หลักแล้วไม่มี `DllImport` คงเหลือ (ไม่รวมโฟลเดอร์ Backup)
- ไม่เปลี่ยนเงื่อนไข foreground ของ Atlas2 หรือพฤติกรรมการวาด
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `7357fd9 refactor: migrate Atlas foreground interop`

### 6.3 Source-generated metadata สำหรับ localization JSON

- เพิ่ม `LocalizationJsonContext` ด้วย `JsonSerializable` ของ .NET 10 สำหรับ schema `Dictionary<string, string>`
- เปลี่ยน localization loader ให้ deserialize ผ่าน generated `JsonTypeInfo` แทน reflection metadata
- การเปลี่ยนนี้ใช้เฉพาะไฟล์ภาษา schema ตายตัว จึงไม่กระทบ config ที่มี tuple, Vector หรือ legacy Newtonsoft attributes
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `8cd48a3 perf: source-generate localization JSON metadata`

### 6.4 เริ่มย้าย JSON runtime ออกจาก Newtonsoft.Json

- ย้าย metadata ของปลั๊กอินไปใช้ source-generated `System.Text.Json` และยังคงการกู้คืนไฟล์ config เสียแบบเดิม
- ย้ายตัวจัดการ temporary files, ตัวตรวจ/ดาวน์โหลดอัปเดตของ Launcher และถอด package Newtonsoft ออกจากโปรเจกต์ Launcher ทั้งหมด
- ย้าย WorldAreaTags ที่เป็น embedded resource ให้ parse แบบ allocation ต่ำด้วย `JsonDocument`
- ย้าย Krangled Passive Detector (เครื่องมือ DEBUG) จาก `JObject` ไปใช้ `JsonNode` พร้อมคงผลลัพธ์ JSON แบบ pretty-print
- เป้าหมายสุดท้ายคือ source ที่ถูก build ทุกส่วนใช้ `System.Text.Json`; โฟลเดอร์ `Backup` จะเก็บของเก่าไว้ตามที่ตกลง และไม่ถูกนำมา build
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `264f6c3 refactor: migrate plugin metadata and launcher JSON`

### 6.5 ย้าย core settings จาก Newtonsoft.Json

- `core_settings.json` ใช้ `StateJsonContext` ที่ source-generate โดย .NET 10 แล้ว
- รองรับ public fields, enum ทั้งตัวเลขและชื่อ, รวมถึง tuple รูปแบบ legacy (`Item1`, `Item2`, …) จึงอ่าน config เดิมของผู้ใช้ได้โดยไม่ต้องลบไฟล์
- ย้าย `[JsonIgnore]` และ callback หลัง deserialize ไปเป็น API ของ `System.Text.Json` (`IJsonOnDeserialized`)
- ถอด Newtonsoft attributes ออกจาก `Rarity` และ `EntityFilterType`; conversion ของ State ใช้ string-enum converter ของ System.Text.Json แทน
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

### 6.6 ย้าย PlayerBuffBar และ PreloadAlert

- ย้าย settings และ data list ของ PlayerBuffBar/PreloadAlert ไปใช้ source-generated `System.Text.Json` metadata โดยรองรับ fields และ Vector เดิม
- ย้าย PlayerBuffBar icon map จาก `JObject` เป็น `JsonDocument` เพื่อลด allocation ของ DOM mutable
- ย้าย `GetForegroundWindow` ใน PlayerBuffBar จาก `DllImport` ไป `LibraryImport`; source หลักจึงไม่มี `DllImport` เหลือแล้ว (ไม่นับ Backup)
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

### 6.7 ย้าย AmanamuVoidAlert, PickupHelper และ WorldDrawing

- ย้าย plugin settings ทั้งสามชุดไปใช้ source-generated `System.Text.Json` metadata
- `WorldDrawing` รองรับ array ของ tuple/Vector ใน config เดิมด้วย `IncludeFields`
- หลังโหลด PickupHelper จะสร้าง hash set/dictionary ด้วย comparer ไม่สนตัวพิมพ์เล็กใหญ่กลับคืน เพื่อคงพฤติกรรม filter เดิม
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

### 6.8 ย้าย HealthBars และ RitualWispAlert

- ย้าย HealthBars settings/config ไปใช้ generated metadata ของ `System.Text.Json`
- ย้าย RitualWispAlert settings และตัวนำเข้าค่า legacy จาก RitualHelper จาก `JObject` เป็น `JsonDocument`
- การย้ายค่าสี Vector จาก config เก่าคง field format เดิมไว้ (`X`, `Y`, `Z`, `W`)
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

### 6.9 ย้าย Atlas2 ออกจาก Newtonsoft.Json

- Atlas2 settings ใช้ source-generated `System.Text.Json` metadata
- biome/content/ritual catalog, token map และ rumours ย้ายไป `JsonSerializer`/`JsonDocument`
- ritual pool และ JSONL diagnostics ใช้ System.Text.Json โดยยังคงรูปแบบข้อมูลเดิม
- ไม่แก้ memory-reader, node cache, parent-climbing หรือ draw path ของ Atlas2 ในชุดนี้
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

### 6.10 ย้าย Radar ออกจาก Newtonsoft.Json

- Radar settings และ icon picker ใช้ generated `System.Text.Json` metadata
- ไฟล์ target list (important, boss arena, stairs) ใช้ System.Text.Json พร้อมคงรูปแบบ JSON เดิม
- ไม่แก้ pathfinding, cache, redraw interval หรือ logic วาด minimap/large map
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

### 6.11 ปิดการย้าย Newtonsoft.Json จาก source ที่ build ทั้งหมด

- ย้าย LootValue จาก Newtonsoft ไป `System.Text.Json`: settings ใช้ generated metadata และการอ่าน API/catalog ใช้ `JsonDocument`/`JsonSerializer`
- ย้าย AutoExile2 web dashboard, profile และ config ไป `System.Text.Json`; เปิด `IncludeFields` เพื่อคงรูปแบบ config แบบ public fields เดิมให้ครบ
- ย้าย AutoHotKeyTrigger และโปรไฟล์ rule ไป `System.Text.Json`; คง discriminator `$type` เดิมของ `Wait` component จึงเปิดไฟล์ profile เก่าได้โดยไม่ต้องสร้างใหม่
- ย้าย `PlaySound` ของ LootValue จาก `DllImport` ไป `LibraryImport`; source หลักไม่มี `DllImport` เหลือแล้ว (ไม่นับ `Backup`)
- เอา generic JSON helper ของ Newtonsoft และ package `Newtonsoft.Json` ออกจาก GameHelper
- ตรวจด้วย source scan แล้วไม่มี `Newtonsoft` ใน source/project ที่ถูก build และ Release build แบบ `--no-restore` ผ่านทั้ง solution: 0 warning / 0 error
- ยืนยันด้วย restore ปกติและ Release build สะอาดแล้ว: 0 warning / 0 error และไม่มี `Newtonsoft.Json.dll` ใน output

### 6.12 ย้าย interop ของ GameHelper หลักเป็น LibraryImport

- ย้าย Win32 window handling ใน `GameProcess` และ interop network/input ใน `MiscHelper` จาก `DllImport` เป็น source-generated `LibraryImport`
- ใช้ `POINT` ที่เป็น blittable ภายในขอบเขต Win32 เพื่อไม่ต้องเปิด runtime marshalling ทั่วทั้งโปรแกรม
- ตั้ง `LangVersion` จาก `Directory.Build.props` เพื่อให้ทุก project ที่ build ภายใต้ solution ใช้มาตรฐาน C# เดียวกัน
- ตรวจ Release build ของ GameHelper ผ่าน 0 warning / 0 error; `DllImport` ไม่เหลือใน GameHelper หรือ Launcher

### 6.13 ปิด DllImport ที่เหลือใน plugin

- ย้าย interop ของ PickupHelper, AutoHotKeyTrigger และ AutoExile2 (Win32 input, XInput และ timer resolution) ไปใช้ source-generated `LibraryImport`
- เปิด `AllowUnsafeBlocks` จาก `Directory.Build.props`; จำเป็นต่อ code ที่ generator สร้างเท่านั้น ไม่ได้เปลี่ยนพฤติกรรมหรือเปิด unsafe API ให้กับ logic ปกติ
- ตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error; interop ส่วนใหญ่ใช้ `LibraryImport` โดยคง `DllImport` เฉพาะ `MiscHelper.SendMessage` ที่ทดสอบแล้วว่าเส้นทาง source-generated ส่งคีย์เข้าเกมไม่เสถียร

### 6.14 บังคับมาตรฐาน .NET 10 สำหรับทุก project

- `Directory.Build.targets` กำหนด `net10.0-windows`, C# 14, nullable และ implicit usings จากส่วนกลาง
- plugin ใหม่จึงรับมาตรฐานเดียวกันโดยอัตโนมัติ; การอัปเกรด framework ในอนาคตเป็นการเปลี่ยนอย่างมีเจตนาจากจุดเดียว
- หลักการ source ใหม่: ใช้ `System.Text.Json` (source generation สำหรับ schema คงที่/hot path), ใช้ `LibraryImport` สำหรับ native interop และไม่เพิ่ม package/API ยุค .NET Framework

### 6.15 Audit dependency หลังย้าย .NET 10

- อัปเดต `System.Linq.Dynamic.Core` ของ AutoHotKeyTrigger จาก 1.7.1 เป็น 1.7.4 และตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error
- แยก ImageSharp 4.x, Vortice/renderer และ AsmResolver 6.x ออกจากชุดนี้ เพราะเป็น major upgrade ที่ต้องทดสอบภาพ overlay/launcher แยกต่างหาก ไม่ใช่ข้อกำหนดของ .NET 10 runtime

### 6.16 ขยาย System.Text.Json source generation สำหรับข้อมูลคงที่

- Launcher ใช้ source-generated metadata สำหรับรายการ temporary file
- Atlas2 ใช้ metadata ที่ generate ล่วงหน้าสำหรับ biome และ content catalog ซึ่งเป็นไฟล์ schema คงที่
- ไม่ย้าย JSON ที่เป็น dynamic profile/web payload แบบฝืน ๆ; เก็บ `JsonSerializerOptions` ไว้สำหรับกรณีเหล่านั้นเพื่อคง compatibility
- ตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error

### 6.17 ย้าย target-list ของ Radar เป็น source-generated JSON

- important POI, boss arena และ stairs target-list ของ Radar ใช้ metadata ที่ generate จาก `RadarJsonContext`
- คงรูปแบบ JSON เดิม (fields/case-insensitive/indentation) และไม่เปลี่ยน logic การอ่านหรือวาด Radar
- ตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error

### 6.18 ย้าย JSON cache และ mapping ของ LootValue เป็น source-generated JSON

- unique-art mapping, path-basename mapping และ price cache ของ LootValue ใช้ metadata ที่ generate ล่วงหน้าจาก `LootValueJsonContext`
- คง option เดิมครบ: อ่านชื่อ property แบบไม่สนตัวพิมพ์, field compatibility และเขียน cache แบบจัดย่อหน้า
- schema ของ price cache และเงื่อนไขลบ/refetch cache เดิมไม่เปลี่ยน จึงยังรับมือไฟล์เก่าหรือไฟล์เสียได้เหมือนเดิม
- ตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error

### 6.19 ย้าย Runeshape catalog ของ LootValue เป็น source-generated JSON

- catalog สูตร Runeshape ที่โหลดจากไฟล์คงที่ใช้ metadata ที่ generate ล่วงหน้าในขอบเขตของ `RuneshapeCatalog`
- การค้นหาไฟล์, schema ของ catalog และ logic คำนวณ offer ยังเป็นชุดเดิมทั้งหมด
- ตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error

### 6.20 ย้าย ritual pool ของ Atlas2 เป็น source-generated JSON

- `ritualmods.json` ซึ่งเป็น catalog คงที่ของ Atlas2 ใช้ metadata ที่ generate ล่วงหน้า
- JSONL diagnostic ของ ritual roll ยังเป็น dynamic snapshot ตามเดิม เพราะเป็นข้อมูล debug ที่ schema ยืดหยุ่นและไม่ใช่เส้นทางใช้งานปกติ
- ตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error

### 6.21 รวมมาตรฐาน project และเปิด nullable จริงทั้ง solution

- ย้าย `TargetFramework`, C# 14, nullable และ implicit usings ไปอยู่ที่ `Directory.Build.props` ซึ่ง SDK อ่านก่อนวิเคราะห์ project; `Directory.Build.targets` ยังคงยืนยันมาตรฐานหลัง project load
- ลบการประกาศซ้ำ/ขัดแย้งใน project เดิม รวมถึง Atlas2 ที่เคยปิด nullable ไว้
- แก้ null-safety ของ Atlas2 ตามความหมายเดิม: ค่า DTO เริ่มต้นที่ปลอดภัย, cache ที่เป็น state ชัดเจน และผลค้นหาที่อาจไม่มีผลลัพธ์เป็น nullable
- เปิด .NET analyzer ระดับล่าสุด และกำหนดให้ Release build ถือ warning เป็น error เพื่อกันมาตรฐานถอยกลับ
- ตรวจ Release build ทั้ง solution ผ่าน 0 warning / 0 error

### 6.22 อัปเดต Launcher จาก AsmResolver 5 เป็น 6

- อัปเดต `AsmResolver.PE.Win32Resources` 5.5.1 เป็น 6.0.1; รุ่นเดิมถูก NuGet ระบุว่า legacy และมี critical bugs
- ปรับ namespace และ API การเขียน version resource ตาม AsmResolver 6 โดยคงขั้นตอน transform executable เดิม
- restore, Release build ทั้ง solution และการตรวจ package deprecated ผ่านครบ 0 warning / 0 error

### 6.23 Audit สถานะ dependency หลังย้ายมาตรฐาน

- การตรวจ NuGet ไม่พบ package vulnerable หรือ deprecated เหลือในทั้ง solution
- รายการที่ใหม่กว่าเหลือเฉพาะ ImageSharp 4.x และ Vortice/renderer 3.8+ ซึ่งเป็น major upgrade ของเส้นทางวาด overlay ทั้งระบบ
- แยก renderer upgrade เป็นชุดทดสอบภาพจริงต่างหาก: ไม่ใช่เงื่อนไขของ .NET 10 และไม่ควรปะปนกับ migration ที่ต้องคงภาพ/การกดผ่านเมาส์เดิม

### 6.24 ตรวจ offset อัตโนมัติและ Local Diagnostics API

- OffsetHelper ตรวจ field sweep และ static pattern หนึ่งครั้งเองหลังเข้าเกมและข้อมูลนิ่ง โดย scan pattern บน background task เพื่อไม่รบกวน render thread
- เพิ่ม API เฉพาะ loopback ที่ `localhost:9877`: อ่านผลตรวจหรือสั่งตรวจซ้ำได้โดยไม่ต้องเปิดหน้าต่าง OffsetHelper
- API ส่งเพียงสถานะ diagnostic และรับคำสั่งตรวจเท่านั้น; ไม่มีคำสั่งควบคุมเกม, ปลั๊กอิน หรือการรับ request จากเครือข่ายภายนอก
- มีคู่มือใช้งานใน `LOCAL_DIAGNOSTICS_API.md`; ตรวจ Release build ของ GameHelper แบบ output แยกผ่าน 0 warning / 0 error ขณะตัวหลักกำลังรัน

### 6.25 เก็บ Memory Diagnostics ผ่าน Local API

- เพิ่มคำสั่ง loopback สำหรับ `memory-reset`, `memory-dump` และ `memory-status` เพื่อเริ่มรอบวัด/เขียน TSV/อ่านสถานะโดยไม่ต้องเปิดหน้าต่าง diagnostics
- เพิ่ม `memory-stop` สำหรับปิด instrumentation หลัง dump เพื่อไม่ให้ตัววัดเพิ่ม overhead ในการใช้งานจริง
- คำสั่งถูก queue แล้วทำงานใน render coroutine เดิม จึงไม่เพิ่มการอ่าน memory หรือ thread ใหม่ใน hot path และยังคง gate การบันทึกด้วย `ShowMemoryDiagnostics`
- dump ใช้ค่า session average, reads/frame และ breakdown scalar/buffer ชุดเดียวกับหน้าต่าง Performance/Memory Diagnostics ทำให้เทียบ workload รอบต่อรอบได้ง่าย

### 6.26 รวมการเดิน UI child paths ต่อเฟรม

- `ImportantUiElements` เคยอ่าน `UiElementBaseOffset` และ child pointer ซ้ำเมื่อหลาย path ใช้ prefix เดียวกัน เช่น world-map tabs และ minimap/แผนที่ใหญ่
- เพิ่ม per-update path resolver ที่แชร์ offset/child pointer ภายในกลุ่มเดียวกัน โดยยัง validate `Self` ของปลายทางทุก path และไม่ลดความถี่การอัปเดต UI
- cache ของ resolver ถูก reuse ต่อ object และ clear ต้นรอบ จึงไม่สร้าง dictionary ใหม่ทุกเฟรมเพิ่มภาระ GC
- หลัง reuse cache แล้ว runtime ยัง validate offset ผ่าน (`6 unchanged, 0 relocated`) และรอบวัด 10 วินาทีไม่มี read failure; `ImportantUiElements` อยู่ราว 96 reads/frame

### 6.27 Hotspots ที่เก็บไว้ทำต่อ

จาก Performance Profiler snapshot ล่าสุด จุดที่ใช้เวลาต่อเฟรมสูงสุด 3 อันดับแรกเป็น pipeline เดียวกันของ entity:

- `GameHelper.RemoteObjects.States.InGameStateObjects.Entity.UpdateData` — **3.36 ms/frame**
- `Entity.UpdateComponentData` — **2.86 ms/frame**
- `Entity.RefreshCachedComponents` — **2.28 ms/frame**

เวลาสามรายการนี้ซ้อนกัน: `UpdateData` ครอบ `UpdateComponentData` ซึ่งครอบ `RefreshCachedComponents` จึงห้ามบวกเป็น 8.50 ms/frame ค่า parent ในภาพคือ 3.36 ms/frame (เวลาสะสมของ scope ไม่ใช่หลักฐานว่า FPS เกมลดเท่ากัน) ควรตรวจ allocation และงานภายใน component ก่อนเปลี่ยนความถี่การ refresh
- Runtime validation: `ImportantUiElements` ลดจากประมาณ 130 เป็น 97 reads/frame และรวมระบบลดจาก 267 เป็น 224 reads/frame; รอบทดสอบไม่มี read failure

### 6.28 ลด closure allocation ใน Entity component lookup

- เปลี่ยน `TryGetComponent<T>` เป็น `ConcurrentDictionary.GetOrAdd` แบบ static factory + argument เพื่อไม่จับ `compAddr` ใน closure ทุกครั้งที่เรียก รวมถึง cache-hit path
- คง semantics ของ cache, การสร้าง component และรอบ refresh เดิม ไม่เพิ่มการอ่าน memory และไม่เปลี่ยน offset
- Release build ทั้ง solution ผ่าน 0 warning / 0 error; ตรวจ IL ของเมธอดแล้วไม่มีการสร้าง closure (เหลือ cached factory delegate ใน cache-miss path)
- ยังไม่ได้วัดผล runtime/GC ในเกมของชุดนี้ จึงยังไม่สรุปเปอร์เซ็นต์ความเร็วหรือผลต่อ FPS

### 6.29 ตัด allocation จาก Render position snapshot

- `Render.UpdateData` เคยสร้าง reference-type snapshot หนึ่งตัวทุกครั้งที่ entity refresh เพื่อให้อ่าน X/Y เป็นคู่ที่สอดคล้องกัน
- เปลี่ยนเป็น pack float X/Y ลง `long` เดียวและใช้ `Interlocked` อ่าน/เขียน จึงคง atomic snapshot เดิมโดยไม่สร้าง object ใน hot path
- profiler จากฉากมอนหนาแน่นแสดงว่า Render ถูก refresh ในเส้นทาง Entity อย่างต่อเนื่อง จึงลด GC pressure ของ `RefreshCachedComponents` ได้โดยไม่ลดความสดตำแหน่ง

### 6.30 ข้าม ObjectMagicProperties ที่ข้อมูลคงที่

- `ObjectMagicProperties` สร้าง rarity, mod names และ mod stats ใหม่เมื่อ component address เปลี่ยนเท่านั้น แต่เดิมยังอ่าน component header ซ้ำทุก entity frame
- เพิ่ม contract ระบุ component ที่ต้อง refresh ทุกเฟรม แล้วให้ ObjectMagicProperties opt out หลัง initial/address-change read
- การเปลี่ยนนี้ไม่ลดการอ่าน Render, Life, Positioned, Targetable, Stats หรือ Buffs ซึ่งมีข้อมูลสดระหว่างต่อสู้
- Runtime validation ในจุดมอนหนาแน่นที่หยุดเกมไว้: entity update ราว 97 ตัวต่อเฟรมเท่าเดิม, `EntityUpdate` ลดจาก 12.4 เป็น 9.3 reads/entity, `AreaInstance.Entities` ลดจาก 1,479 เป็น 1,175 reads/frame และรวมลดจาก 1,664 เป็น 1,307 reads/frame โดยไม่มี read failure

### 6.31 ตัด closure allocation ระหว่างรวม Buffs

- `Buffs.UpdateData` และ `AddSyntheticStatusEffect` ใช้ `ConcurrentDictionary.AddOrUpdate` ด้วย lambda ที่จับ status effect ของรอบนั้น ทำให้สร้าง closure ทุก effect
- เปลี่ยนเป็น static factory ของ .NET สมัยใหม่ โดยส่ง status effect เป็น argument ตรง คง atomic update และกฎรวม stack/time-left เดิม
- factory ที่ค้นชื่อบัฟจาก GGPK cache ก็เป็น static แล้ว จึงไม่จับ instance ของ `Buffs` ทุกครั้งที่ lookup (รวมถึง cache hit)
- pointer vector ของ status effects เปลี่ยนเป็น pooled buffer ด้วย จึงคง bulk read เดิมแต่ไม่สร้าง `IntPtr[]` ใหม่ทุก update

### 6.32 ใช้ pooled buffer สำหรับ stat vectors

- `Stats` อ่าน native stat vector ของทุก entity ในแต่ละรอบ แล้ว array เดิมถูกทิ้งทันที ทำให้เกิด allocation หลายร้อยไบต์ต่อครั้งแม้ API ต่อปลั๊กอินไม่เปลี่ยน
- `StatUpdator` จึงยืม `ArrayPool<StatArrayStruct>` เพื่ออ่าน buffer เป็นก้อนเดียว คัดลง dictionary เดิม แล้วคืน buffer ทันที
- ไม่ลดความถี่/เนื้อหาของ stat และไม่เปลี่ยน public API ของปลั๊กอิน; กรณี vector หรือการอ่านผิดพลาดยัง clear dictionary เช่นพฤติกรรมเดิม

### 6.33 ใช้ pooled buffer สำหรับ Actor vectors

- `Actor.UpdateData` สร้าง array ใหม่เพื่ออ่าน cooldowns, active skills และ deployed entities ทุก update
- เพิ่ม helper ภายในสำหรับอ่าน native vector ลง `ArrayPool` แล้วใช้กับทั้งสาม vector; คงจำนวน bulk read และข้อมูลที่ public API ของปลั๊กอินเห็นไว้เท่าเดิม
- การค้นชื่อ active skill จาก GGPK cache ใช้ static factory เพื่อไม่สร้าง closure ในทุก active-skill lookup

### 6.34 ใช้ cached Stats ระหว่างคำนวณสถานะ entity

- `CalculateEntityState` เคยสร้าง `Stats` component ใหม่แบบไม่ cache ทุก entity frame เพียงเพื่อตรวจ `is_dead` แม้ `UpdateComponentData` สามารถ refresh cached component ในรอบเดียวกันได้
- เปลี่ยนให้ใช้ cached `Stats`; เมื่อมีครั้งแรกจะสร้างและอ่านครบตามเดิม จากนั้น refresh instance เดิมก่อนคำนวณ state ทุก frame
- ตอน classify monster ก็ cache `Stats` และ `ObjectMagicProperties` ตั้งแต่แรก เพื่อไม่สร้างข้อมูลเดิมซ้ำใน state calculation
- มอนสเตอร์ที่ถูกพัก (`Useless`) ยังคงสร้าง `Stats` สดในรอบ wake-up ทุก 30 frames เช่นเดิม เพื่อไม่ใช้ค่า `is_dead` เก่าเมื่อมอนสเตอร์กลับมาใช้งาน

### 6.35 cache ชื่อ component ตอนสร้าง component map

- `RefreshComponentMap` อ่าน string ชื่อ component ซ้ำจาก native memory ทุกครั้งที่ entity ต้องสร้าง/รีเฟรช map
- ชื่อ component อยู่ใน GGPK/static data จึงใช้ `GgpkStringCache` ที่ถูกล้างตาม area/game lifecycle อยู่แล้ว; ลดทั้ง read และ string allocation โดยไม่ลดรอบ refresh ของ entity

### 6.36 pool vectors ระหว่างสร้าง entity component map

- `RefreshComponentMap` อ่าน vector ของ `(component name pointer, index)` และ vector component-address เป็น array ชั่วคราวสำหรับ entity ทุกตัวที่ต้อง rebuild map
- เปลี่ยนสอง vector เป็น `PooledNativeVector`; สร้าง map จาก entry count จริงและคืน buffer ทันทีหลังใช้ โดยคง validation ของ `StdBucket` และรูปแบบข้อมูลเดิม

### 6.37 pool buffer สำหรับ external std::wstring

- การอ่าน `std::wstring` ที่อยู่นอก inline storage เคยสร้าง byte array ชั่วคราวก่อน decode เป็น string ทุกครั้ง
- เปลี่ยน byte buffer เป็น `ArrayPool<byte>` โดยใช้ `TryReadMemoryArray` เดิมและคืน buffer ใน `finally`; string ผลลัพธ์และกรณีอ่านพลาดยังเหมือนเดิม

### 6.38 รวม scalar reads ของ component ที่อยู่ติดกัน

- เพิ่ม read-through window แบบ thread-local ใน `SafeMemoryHandle`: อ่านช่วง memory ที่อยู่ติดกันเป็น buffer ครั้งเดียว แล้วให้ `ReadMemory<T>`/`TryReadMemory<T>` ของ component ภายในช่วงใช้ข้อมูลจาก buffer โดยไม่ยิง kernel call ซ้ำ
- `Entity.RefreshCachedComponents` จัดกลุ่ม component ที่ต้อง refresh ทุกเฟรมตาม address เฉพาะเมื่อมีอย่างน้อย 3 ตัว, gap ไม่เกิน `0x100` bytes และ span ไม่เกิน `0x8000` bytes; กลุ่มที่ไม่เข้าเงื่อนไขยังใช้ลูปเดิม
- ถ้า batch read อ่านไม่ได้ จะกลับไป scalar read เดิมทันที จึงไม่ลดความถี่/ไม่เปลี่ยน public component หรือ plugin API และยังตรวจ `IsParentValid` ทุก component เหมือนเดิม
- Runtime validation ฉากมอนหนาแน่น: รวมลดจาก `1327` เป็น `1295 reads/frame` (~2.4%), `EntityUpdate` จาก `8.3` เป็น `8.0 reads/entity`, 60.6 frames/s และ `0 failures`
- เป็นการลดจำนวน kernel transition ไม่ใช่การลดรอบอัปเดตหรือการลดข้อมูลที่ปลั๊กอินได้รับ

### 6.39 รวม reads ของ entity std::map แบบเป็น wave

- `AreaInstance` เปลี่ยนเส้นทาง awake-entity map มาใช้การอ่าน node แบบ breadth-first wave: sort address ของ node ใน wave เดียวกัน แล้วอ่านช่วงที่ติดกันเป็น buffer ก่อนแยก `StdMapNode` ฝั่งเรา
- เริ่มจาก `head.Parent` (root จริง), กัน `head` sentinel และ `visited` node เพื่อไม่เดินวนใน red-black tree; ตรวจ `Color` และ address ก่อนนำ node ไปใช้
- ถ้า batch span อ่านไม่ได้, node ในกลุ่มนั้นกลับไป `TryReadMemory<StdMapNode<...>>` รายตัวทันที จึงยังรองรับ heap ที่กระจัดกระจายและ page boundary
- ไม่ลด callback หรือความถี่ entity update: callback เดิมยังทำ `entity.Address` และ `UpdateNearby` ทุก node ที่ valid เหมือนเดิม
- Runtime validation จุดมอนหนาแน่น: รวมระบบลดจาก `1327` เป็น `1083 reads/frame` (~18.4%), `60.2 frames/s`, `0 failures`; `EntityUpdate` อยู่ที่ `7.9 reads/entity`
- ชุดทดลอง map แบบเริ่มต้นผิด root ทำให้เกิด sentinel loop ถูกถอดทิ้งแล้ว; โค้ดที่บันทึกใช้ root/sentinel guard และผ่าน build + runtime diagnostics แล้ว

### 6.40 ลด overhead ของ component refresh ต่อเฟรม

- เพิ่ม `RemoteObjectBase.RefreshDataNow()` สำหรับ refresh object ที่ address เดิม โดยยังใช้ execution lock, exception boundary และ profiler scope เดิม แต่ไม่ผ่าน `Address getter/setter` ที่ต้องตรวจ address ซ้ำ
- `Entity` เก็บลำดับ component ที่ sort แล้วไว้จนกว่า component map/cache จะเปลี่ยน จึงไม่ sort และไม่ rent/clear array ใหม่ทุก entity frame
- ไม่ลดจำนวน component, memory read หรือความถี่ update; ทุก component ที่ `RequiresPerFrameRefresh` ยังถูก refresh ทุกเฟรมและตรวจ owner entity เหมือนเดิม
- Runtime profiler ที่ 165 FPS, 104 entities: `RefreshCachedComponents` `1.01 ms → 948 µs`, allocation `356 B → 203 B`; `UpdateComponentData` `1.28 → 1.22 ms`

### 6.41 ใช้ CollectionsMarshal ในการเติม Stats

- `StatUpdator` ยังคงใช้ dictionary เดิมที่ plugin อ่านอยู่ แต่เปลี่ยนการเขียนแต่ละ stat เป็น `CollectionsMarshal.GetValueRefOrAddDefault` ของ .NET 10 เพื่อลด lookup ซ้ำในวงวนร้อน
- คงการ lock, `Clear`, การคืน `ArrayPool` และพฤติกรรมกรณีอ่าน vector ล้มเหลวเหมือนเดิม
- ไม่ลดการอ่านหรือความถี่ update; เปลี่ยนเฉพาะวิธีเติมค่าใน dictionary ฝั่ง process

### 6.42 แยก expected torn-read ของ LoadedFiles ออกจาก failure จริง

- `LoadedFiles.AddFileIfLoadedInCurrentArea` เดิมใช้เส้นทางที่บันทึก failure เมื่อ node ของ loaded-file map ถูกเกมแก้พร้อมกัน ทำให้ dump แสดง `1024` failures ทั้งที่ session read สำเร็จทั้งหมด
- เปลี่ยนจุดนี้เป็น `TryReadMemory(..., recordFailure: false)` และข้าม node ที่อ่านไม่ทันอย่างเงียบๆ เพราะข้อมูลรายการไฟล์เป็น best-effort อยู่แล้ว
- เส้นทางอื่นยังบันทึก failure ตามเดิม; runtime validation ลด `Total failed reads: 1024 → 0`, session failures `0`, และยังคงประมาณ `1079 reads/frame`

### 6.43 เพิ่ม Entity Frame Snapshot (ถูกรวมเข้า NewMemoryRead แล้ว)

- สร้าง read plan ของ component ที่ refresh ทุกเฟรม แล้ว reuse scalar reads จาก snapshot เดียวกันภายใน entity update รอบนั้น
- ถ้า component ถูกสร้างใหม่, address เปลี่ยน หรือ span อ่านไม่ผ่าน จะกลับไปใช้ per-entity batch/scalar path เดิมทันที; ไม่ลดจำนวนรอบ update และไม่เปลี่ยนข้อมูลที่ entity/plugin เห็น
- เพิ่ม `SafeMemoryHandle.ReadCachePlan` แบบ thread-local ใช้ `ArrayPool<byte>` และ binary-search windows เพื่อไม่เพิ่ม global lock ใน hot path
- Runtime validation จุดเดิม: เปิด snapshot อย่างเดียวลดประมาณ `1079 → 1044 reads/frame` (~3.2%), `0 failures`; ผลขึ้นกับ locality ของ component heap จึงปิดไว้ก่อนเป็น baseline ที่ปลอดภัย

### 6.44 เพิ่ม Wide Entity-map Batches (ถูกรวมเข้า NewMemoryRead แล้ว)

- `ReadStdMapBatched` ขยาย gap ที่ยอมรวมจาก `0x200` เป็น `0x10000` และ span สูงสุด `64 KiB` เป็น `1 MiB` เมื่อใช้ reader ใหม่
- การอ่านยังเป็น best-effort: ถ้า range ใหญ่คร่อม page ที่อ่านไม่ได้ จะ fallback กลับไปอ่าน node scalar ทุกตัวในกลุ่มนั้น และ guard เดิม (`Color`, pointer, visited) ยังอยู่ครบ
- Runtime validation จุดเดิม: wide map อย่างเดียวได้ประมาณ `1013 reads/frame`, `177.1 frames/s`, `0 failures`; เปิดร่วมกับ snapshot ได้ `978 reads/frame`, `176.6 frames/s`, `0 failures` เทียบ baseline `1079 reads/frame`
- trade-off ที่วัดได้คือ requested throughput เพิ่มจากประมาณ `59` เป็น `129 MiB/s` เพราะอ่าน byte ที่ไม่ใช่ node มากขึ้น แต่ kernel transition ลดลงและ frame throughput ไม่ลด จึงยังปิด flag ไว้ให้ผู้ใช้เปิดทดสอบตามฉากจริง

ผล benchmark เดิมของสองส่วนยังเก็บไว้เป็นข้อมูลอ้างอิง แต่ไม่มี checkbox แยกแล้ว เพื่อไม่ให้ระบบมีหลายโหมดซ้อนกัน

### 6.45 รวมเป็น NewMemoryRead แบบเปิด/ปิดได้

- เพิ่ม `State.EnableNewMemoryRead` เป็น master switch เดียวสำหรับ redesign ระบบอ่าน memory รอบเฟรม
- เมื่อเปิด master switch จะเปิดทั้ง `ReadCachePlan` ของ component และ wide entity-map batches โดยไม่ต้องแก้ plugin หรือเรียก API ใหม่
- เมื่อปิด master switch จะกลับไปใช้เส้นทางเดิมทั้งหมด; benchmark แยกส่วนทำเสร็จแล้วจึงไม่จำเป็นต้องคง flag ย่อยไว้
- ทุก batch ยังคงมี fallback scalar, address validation และ diagnostics เดิม จึงใช้ทดสอบแบบค่อยเป็นค่อยไปได้

### 6.46 แยก orchestration เป็น `FrameMemoryReadPipeline`

- ย้ายการสร้าง component read ranges และอายุของ `ReadCachePlan` ออกจาก `AreaInstance` มาไว้ใน `GameHelper/Utils/FrameMemoryReadPipeline.cs`
- pipeline เป็นจุดกลางของ read phase ต่อเฟรม: planner รวบรวม address, reader เปิด pooled windows, แล้วจึงให้ entity update ใช้ snapshot; parser/entity code ยังใช้ object เดิม
- `EnableNewMemoryRead` เป็นจุดตัดเดียวของ pipeline ทำให้ปิด master แล้วกลับ legacy path ได้โดยไม่ต้องมีโค้ดสองชุดกระจายทั่วระบบ
- นี่เป็นฐานสำหรับย้าย UI/entity readers ชุดถัดไปเข้า planner โดยไม่เปลี่ยน plugin API หรือความถี่การ update

### 6.47 ยก pipeline อ่าน memory เป็นระดับทั้ง frame และเพิ่ม diagnostics API ตรง

- ย้ายการเปิด `FrameMemoryReadPipeline` ไปไว้ที่ `InGameState.UpdateData` ทำให้ Area, World, UI และ entity readers ใน frame เดียวกันใช้ read context เดียวกัน; `AreaInstance` จะสร้าง pipeline เองเฉพาะกรณีถูกเรียกแยกนอก frame orchestration เท่านั้น
- เพิ่ม lazy page cache สำหรับ scalar address ที่ planner ยังไม่รู้ล่วงหน้า: อ่านเป็น page 4 KiB หรือ 8 KiB เมื่อข้าม page, จำกัดไม่เกิน 2,048 windows ต่อ frame และคืน buffer ผ่าน `ArrayPool<byte>` เมื่อจบ frame; ถ้าอ่านไม่ได้จะ fallback ไป scalar read เดิม
- คง `EnableNewMemoryRead` เป็น master switch เดียว ไม่มี checkbox ย่อย และไม่เปลี่ยนความถี่ update หรือข้อมูลที่ปลั๊กอินเห็น
- เพิ่ม loopback API ที่อ่านค่าปัจจุบันโดยตรง ไม่ต้อง screenshot/clipboard/dump:
  - `GET /api/diagnostics/memory-snapshot`
  - `GET /api/diagnostics/performance-snapshot`
  - `POST /api/diagnostics/memory-reset`
  - `POST /api/diagnostics/performance-reset`
- `performance-snapshot` รองรับทั้ง session aggregate และ `Current Frame Only` แบบเดียวกับหน้าต่าง profiler; reset ผ่าน API ไม่ต้องเปิดหน้าต่างก่อน
- Runtime validation หลังย้าย pipeline ทั้ง frame: `0` failed reads, ประมาณ `176.7 FPS`, `671.7 reads/frame`, `350.8 MiB/s` ในจุดทดสอบเดิม (ตัวเลขขึ้นกับเกม/ฉากและจำนวน entity)
- Snapshot API ทำให้การวิเคราะห์รอบถัดไปใช้ตัวเลขจาก process โดยตรง และยังเก็บ dump endpoint เดิมไว้เป็นทางเลือกสำหรับ archive เท่านั้น

### 6.48 ลด lock contention ใน address read ของ remote objects

- `RemoteObjectBase.Address` เป็น pointer ที่อ่านบ่อยที่สุดใน component refresh path; getter เดิมล็อก monitor ทุกครั้ง แม้การเปลี่ยน address จะยังถูก serialize อยู่แล้ว
- เปลี่ยน getter เป็น `Volatile.Read(ref address)` บน x64 และคง lock ไว้ใน setter/rebind กับ `updateExecutionLock` ตอน refresh เพื่อไม่ให้การเปลี่ยน pointer แข่งกับ lifecycle mutation
- ไม่เปลี่ยนชนิดข้อมูล, ความถี่ update หรือ public plugin contract; ลดเฉพาะ synchronization overhead ของการอ่าน pointer
- Runtime validation: `Entity.UpdateData` ประมาณ `14.2 → 13.4 us/call`, `UpdateComponentData` `11.6 → 10.9 us/call`, `RefreshCachedComponents` `8.8 → 8.2 us/call`, `0 failed reads`

### 6.49 ไม่ retry component-map rebuild ซ้ำทันทีเมื่อ entity กำลังตื่น

- ในฉากที่ entity ตื่นพร้อมกันจำนวนมาก `EntityDetailsPtr` อาจเป็น pointer torn ในจังหวะเดียวกับที่เกมกำลังเติม component map
- โค้ดเดิมเรียก `UpdateComponentData(..., true)` ซ้ำทันที แม้ความพยายามแรกก็เป็น full map rebuild แล้ว ทำให้เดิน pointer ที่ไม่เสถียรซ้ำและเพิ่ม native reads/allocations โดยไม่ได้เพิ่มโอกาสสำเร็จ
- เปลี่ยนให้ retry เต็มเฉพาะกรณี per-frame component refresh ล้มเหลว; ถ้า full map rebuild ล้มเหลวจะปล่อย entity unresolved และลองใหม่ใน frame ถัดไปตาม lifecycle เดิม
- ไม่ลดความถี่ update ของ entity ที่ valid และไม่เปลี่ยนข้อมูล plugin; ลดเฉพาะ duplicate recovery work จาก torn read
- Stress validation หลัง reset: จากประมาณ `1,522 → 1,105 reads/frame`, รอบใหม่อยู่ราว `166 FPS` และ `0–2 native failures`; ค่า calls/s และ MiB/s เปลี่ยนตามจำนวน entity ที่ตื่นในช่วงวัด และ `Entity.UpdateComponentData` ลงมาราว `5.4 us/call`

### 6.50 ทำให้ plugin loading deterministic และรองรับ native dependency

- โหลดและสร้าง plugin ตามลำดับชื่อบน thread เดียว เพราะงานนี้เกิดเฉพาะตอนเริ่มหรือ reload และ constructor ของ plugin ไม่ควรถูกเรียกพร้อมกันโดยไม่แจ้งสัญญาไว้
- unload collectible `AssemblyLoadContext` เมื่ออ่าน assembly หรือสร้าง plugin ล้มเหลว เพื่อลด assembly/file handle ที่ค้างหลัง reload
- เพิ่ม `ResolveUnmanagedDllToPath` ให้ dependency native ที่อยู่ในโฟลเดอร์ plugin ถูกโหลดผ่าน resolver เดียวกับ managed dependency
- คำสั่งโหลด plugin ใน Debug ใช้ชื่อเท่ากันแบบไม่สนตัวพิมพ์ แทนการจับชื่อบางส่วนที่อาจโหลดผิดโฟลเดอร์

### 6.51 ยึด runtime files กับโฟลเดอร์โปรแกรม

- กำหนด path ของ core settings, plugin metadata และโฟลเดอร์ plugin จาก `AppContext.BaseDirectory` เพื่อไม่ให้เปลี่ยนตาม current working directory ที่ใช้เปิดโปรแกรม
- ให้ Launcher เริ่ม GameHelper โดยตั้ง working directory เป็นโฟลเดอร์ติดตั้งอย่างชัดเจน ครอบคลุม resource เก่าที่ยังใช้ relative path
- เขียน `Error.log` ไว้ข้าง executable และป้องกันความผิดพลาดระหว่างเขียน log ไม่ให้บดบัง exception ต้นเหตุ
- รับ path ที่ผู้ใช้ป้อนให้ Launcher อย่างปลอดภัย รวมถึง path ที่มีเครื่องหมายคำพูด และจบอย่างเรียบร้อยเมื่อเว้นว่างหรือเข้าถึงไม่ได้

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
