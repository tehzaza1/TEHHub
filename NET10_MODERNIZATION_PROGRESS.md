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

## กำลังทำ

เฟส 5 — แทนที่ ProcessMemoryUtilities.Net: core reader ย้ายแล้ว กำลังรอ runtime validation ด้วย GameHelper/PoE2 ก่อนย้าย Atlas2 และถอด package

## ลำดับถัดไป

### 5. แทนที่ ProcessMemoryUtilities.Net

แพ็กเกจ `ProcessMemoryUtilities.Net 1.3.4` เป็น dependency เก่าจากปี 2020 และอยู่ในเส้นทางสำคัญของโปรแกรม แผนคือ:

1. สร้าง native memory reader ภายในโปรเจกต์ด้วย `LibraryImport`
2. คง API ของ `SafeMemoryHandle` ไว้ก่อน เพื่อไม่ให้ส่วนอื่นต้องเปลี่ยนพร้อมกัน
3. เทียบความถูกต้อง จำนวน calls, throughput, latency และ failures กับของเดิม
4. ลด allocation ของ array/string reads และใช้ buffer ซ้ำในจุดที่ปลอดภัย
5. รวม read ที่อยู่ติดกันเพื่อลดจำนวนการข้ามจาก .NET ไป Windows
6. ย้าย Atlas2 ซึ่งยังเรียก wrapper เก่าโดยตรง
7. ถอด package หลังทุกจุดผ่านการทดสอบ

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
