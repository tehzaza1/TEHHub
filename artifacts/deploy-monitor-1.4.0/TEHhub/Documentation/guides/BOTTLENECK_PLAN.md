# แผนลดคอขวด TEHhub

ผู้ใช้รายงานว่าเล่นแมพจริง 5–6 ชั่วโมงและทุกปลั๊กอินทำงานปกติ ข้อมูลนี้สนับสนุนความเสถียร แต่ยังไม่มีตัวเลขยืนยันว่าส่วนใดเป็นคอขวด ตารางด้านล่างเป็นจุดที่ควรวัดจากโค้ด ไม่ใช่ผล benchmark ในเกม

## เก็บหลักฐานก่อนแก้

ใช้ Debug build เพื่อเก็บข้อมูลภายในโปรแกรมผ่าน Local Diagnostics API ที่ `http://localhost:9877` และตัวเก็บข้อมูล [Capture-Bottlenecks.ps1](../../tools/Capture-Bottlenecks.ps1) ไม่ต้องอ่านภาพหน้าจอหรือคัดลอกตารางด้วยมือ

- `POST /api/diagnostics/capture-start`: เริ่มรอบเก็บข้อมูลและเปิด instrumentation ชั่วคราว
- `GET /api/diagnostics/bottleneck-snapshot`: อ่าน snapshot ที่โปรแกรมสร้างบน main thread ประมาณหนึ่งครั้งต่อวินาที การเรียก HTTP ไม่เดิน entity/UI tree เพิ่มเอง
- `POST /api/diagnostics/capture-stop`: จบรอบและคืนสถานะตัววัด รอบเก็บข้อมูลมีเพดาน 120 วินาทีเพื่อไม่เปิด instrumentation ทิ้งไว้

เก็บแยกรอบตามสถานการณ์: ยืนใน hideout, เล่นแมพที่มีมอนสเตอร์จำนวนมาก, เก็บของจำนวนมาก, เปิด stash/inventory, เปิด Atlas และเลื่อน/ซูม, Atlas Passive ในโหมดจอย และเปลี่ยนแมพ ห้ามนำค่าเฉลี่ยของทุกสถานการณ์รวมกันแล้วใช้ตัดสินว่าฟีเจอร์หนึ่งช้า

อ่านตัวเลขร่วมกัน:

- ระยะเวลา Render ที่วัดคือ elapsed duration ของงานฝั่ง CPU ใน TEHhub ไม่ใช่ FPS ของเกมหรือเวลาที่ GPU present ภาพ และมีเวลารอภายใน scope ได้
- CPU process ใช้บอกภาระรวม รวม worker threads; RAM, allocation และจำนวน GC ใช้ตรวจว่าค่าเพิ่มต่อเนื่องหรือมี spike
- Memory calls/s, MiB/s, reads/frame และ failures บอกต้นทุนการอ่าน ส่วน profiler บอกชื่อส่วนงาน เวลาเฉลี่ย/P95/P99 และ allocation ต่อ call
- ค่าเฉลี่ย profiler ครอบคลุมรอบเก็บข้อมูล แต่ percentile ของแต่ละ method ใช้หน้าต่างล่าสุดที่มีขนาดจำกัด จึงต้องอ่านหลาย snapshot และอย่าอ้างเป็น percentile ของทั้งรอบ
- Memory regions และ profiler scopes ซ้อนกันได้ ห้ามบวก parent กับ child เป็นต้นทุนรวม เพราะจะนับงานเดียวกันซ้ำ ค่า allocation ต่อ thread ไม่รวม allocation ที่ worker thread ทำให้ parent
- Debug มี overhead ต่างจาก Release และ instrumentation มี stopwatch, counters, concurrent collections และการสร้าง snapshot ต้องเทียบรอบเปิด/ปิดตัววัด และยืนยันผล optimization ด้วย Release ก่อนสรุปประโยชน์จริง

## ลำดับงานหลังได้ข้อมูล

| ลำดับตรวจ | จุดในโค้ด | หลักฐานที่ต้องดู | แนวทางเมื่อยืนยันว่ามีคอขวด |
| --- | --- | --- | --- |
| 1 | `TEHhub/RemoteObjects/States/InGameStateObjects/AreaInstance.cs`: `UpdateData`, `UpdateEntities`; `TEHhub/Utils/FrameMemoryReadPipeline.cs`; `TEHhub/Utils/SafeMemoryHandle.cs` | `Core.AreaInstance`, `Core.AreaInstance.Entities`, `Core.AreaInstance.EntityUpdate`, reads/frame, CPU และ entity workload | ตรวจการอ่าน component ซ้ำและการ prefetch หน้า memory; ใช้ข้อมูลร่วมใน frame เดียวก่อนเพิ่ม cache ข้าม frame; วัดต้นทุน `Parallel.ForEach` ในรอบ cleanup ก่อนทดลองลดงาน/จำกัด parallelism |
| 2 | `Plugins/Radar/Radar.cs`: `CollectEntityPaths`, `RebuildEntityPaths`, `RebuildTrackedNodes`, `DrawDirectionLines`; `Plugins/Radar/Pathfinder.cs` | profiler ของ Radar, process CPU และการเพิ่ม CPU เมื่อเปิด pathfinding | เพิ่ม scope ภายใน worker A* ก่อนสรุป เพราะ scope ที่เรียก `Task.Run` วัดได้เพียงการเตรียมและส่งงาน; จำกัดจำนวนเป้าหมาย/งบเวลารอบคำนวณ ใช้ cache เส้นทางและ cancellation เมื่อเปลี่ยนพื้นที่ |
| 3 | `Plugins/Atlas2/Atlas2.cs`: `DrawUI`, `RefreshNodeCache` | spike ตอนเปิด Atlas/เลื่อน/ซูม; allocation; memory ของ `Atlas2.DrawUI` | แยก refresh ข้อมูล static, topology/BFS และสถานะ node; cadence เดิม 20 frames ขึ้นกับอัตรา render ทดลองเวลาแทนเมื่อจำเป็น; reuse `allCenters`/scratch lists แทนสร้าง dictionary และ LINQ ทุก frame โดยยังอ่านตำแหน่ง UI สด |
| 4 | `Plugins/LootValue/LootValueCore.cs`: `DrawUI`, `ReadFreshItem`, ground/tag/alert และ slot scans | ค่าเพิ่มเฉพาะของบนพื้นจำนวนมากหรือเปิด stash; reads/frame กับ allocation | ระบบมี scan intervals อยู่แล้ว ตรวจ item เดียวถูกอ่านหลายรอบหรือไม่ แล้วแชร์ snapshot ภายในรอบสแกน; แยก pricing/static metadata จากตำแหน่ง/scroll ที่ต้องสด ไม่ลด refresh ทุกอย่างพร้อมกัน |
| 5 | `TEHhub/Ui/PerformanceProfiler.cs`, `TEHhub/Ui/MemoryReadDiagnostics.cs` และ diagnostics snapshot | ความต่างระหว่างเปิด/ปิด capture และต้นทุนสร้าง snapshot | cache/sort snapshot ตามช่วงเวลา, ไม่ทำ JSON serialization หรือสร้างตารางทุก frame; ลด overhead ตัววัดหลังยืนยันตัวเลข โดยยังเก็บข้อมูลที่ใช้เปรียบเทียบได้ |

ลำดับจริงให้เรียงจาก CPU duration/allocations และ spikes ที่วัดได้ในสถานการณ์ที่ผู้ใช้เล่นบ่อย ส่วนที่ไม่แพงไม่ต้องแก้เพียงเพราะโค้ดยาว

## ข้อจำกัดที่ต้องรักษา

1. Cache ของ entity/component ต้องผูก area hash/address และ pointer identity; เปลี่ยนแมพ, process หลุด, entity rebound หรือ item เปลี่ยนต้อง invalidate อย่าเก็บข้อมูล health/buff หรือ item state ข้าม frame โดยไม่มี freshness rule
2. SleepingEntities เป็นงานสำรวจที่ต้องจำกัดงบและทำ incremental ระบบมี `CreateSleepingEntityScan` อยู่แล้ว ไม่เพิ่ม full sleeping-map scan ทุก frame และไม่ถือ sleeping entity ทุกตัวเป็น entity ที่ปลั๊กอินต้องประมวลผล
3. Worker ของ Radar ต้องใช้ snapshot ส่วนตัวและตรวจ generation ของแมพก่อนส่งผลกลับ การเปลี่ยนพื้นที่ต้องไม่ยอมให้ผลเส้นทางของแมพเก่าเขียนทับ cache ใหม่
4. Atlas ในโหมดจอยมี ghost visibility: Atlas Map อาจยัง visible ตอน Atlas Passive เปิด ต้องรักษาการตรวจ passive panel และซ่อน overlay การลด UI reads ต้องไม่ตัดเงื่อนไขนี้ทิ้ง
5. Failure ระหว่างโหลด/เปลี่ยนพื้นที่มีได้ ตรวจรูปแบบและการเกิดซ้ำก่อนสรุปว่า offset เสีย ห้ามลด failures ด้วยการกลืน exception หรือหยุดอ่านข้อมูลที่ฟีเจอร์ต้องใช้

## เกณฑ์ส่งมอบ optimization แต่ละรอบ

แก้หนึ่งกลุ่มงานต่อรอบ เก็บ baseline กับหลังแก้ด้วย settings/ปลั๊กอินและ workload ใกล้เคียงกัน ตรวจระยะเวลาเฉลี่ยและ tail spikes, CPU, allocation/GC, RAM, memory failures พร้อมความถูกต้องของฟีเจอร์ หากผลไม่ชัดให้รายงานว่ายังไม่ยืนยัน แทนตั้งเปอร์เซ็นต์ประหยัดจากการเดา

Build/tests ต้องผ่าน และตรวจฟีเจอร์ที่ได้รับผลโดยตรง เช่น Radar เปลี่ยนแมพแล้วไม่ใช้ทางเก่า, LootValue ราคา/จำนวน item เปลี่ยนได้, Atlas badge และ route ยังถูกเมื่อเปิด Passive ทุกชุดที่ส่งมอบต้องเพิ่มเวอร์ชัน `x.x.x` และ CHANGELOG ตาม [นโยบายโครงการ](../../AGENTS.md) โดยแยกการทดสอบจริงออกจากสิ่งที่ยังไม่ได้ทดสอบ
