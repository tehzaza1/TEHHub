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

พบสาเหตุของ call rate ที่สูง: ค่า `FPSLimit = 60` ถูกโหลดจาก settings แต่ไม่ได้ส่งให้ overlay ตอนเริ่มโปรแกรม ทำให้ event/update loop อาจทำงานไม่จำกัดจนกว่าผู้ใช้จะแก้ค่าในหน้าตั้งค่า

- แก้ `GameOverlay.Run()` ให้ใช้ FPS limit ที่บันทึกไว้ตั้งแต่เริ่ม
- การจำกัดนี้มีผลเฉพาะ GameHelper overlay ไม่ได้จำกัด FPS ของเกม
- ตรวจ Release build ผ่านทุกโปรเจกต์ 0 warning / 0 error

Commit: `7e2956f perf: apply overlay frame limit at startup`

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
