# Offline Memory Read Policy Simulation Report (Production Planned-Range Model)
Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead (with Production Planned Ranges) vs. Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)

Cost Model: `EstimatedCost = (NativeCalls * CallCost) + (TotalFetchedBytes * 1.0)`

## 1. Production Frame Component Range Planner Invariants
The simulator faithfully implements `TEHhub.Utils.FrameMemoryReadPipeline.BuildRanges` rules:
- `MinComponentsPerRange = 3`
- `MaxGapBetweenComponents = 0x100` (256 bytes)
- `RangeTailBytes = 0x800` (2048 bytes)
- `MaxRangeBytes = 0x8000` (32768 bytes)

## 2. Heavy Scene Simulations (Monster + Ground Item Scaled Workloads)

### Scene: 500 Monsters + 500 Ground Items (1,000 Total Entities)

#### Locality Distribution: **Clustered**

##### Scale: 1000 Entities | Locality: Clustered | Consumers: 1 (10,000 reqs, 1000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 10,000 | 10,000 | 0 B | 0 B | 624,000 B | 1.00x | 35.7 MB/s | 71.4 MB/s | 85.7 MB/s | 107.1 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 10,000 | 3,450 | 2,496,000 B | 4,292,608 B | 6,881,664 B | 11.03x | 393.8 MB/s | 787.5 MB/s | 945.1 MB/s | 1.15 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 10,000 | 4,506 | 2,496,000 B | 516,096 B | 3,652,496 B | 5.85x | 209.0 MB/s | 418.0 MB/s | 501.6 MB/s | 627.0 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 1000 Entities | Locality: Clustered | Consumers: 3 (30,000 reqs, 1000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 | 30,000 | 0 B | 0 B | 1,872,000 B | 1.00x | 107.1 MB/s | 214.2 MB/s | 257.1 MB/s | 321.4 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 30,000 | 6,254 | 2,496,000 B | 4,292,608 B | 7,067,776 B | 3.78x | 404.4 MB/s | 808.8 MB/s | 970.6 MB/s | 1.18 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 30,000 | 4,506 | 2,496,000 B | 516,096 B | 3,652,496 B | 1.95x | 209.0 MB/s | 418.0 MB/s | 501.6 MB/s | 627.0 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 1000 Entities | Locality: Clustered | Consumers: 5 (50,000 reqs, 1000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 50,000 | 50,000 | 0 B | 0 B | 3,120,000 B | 1.00x | 178.5 MB/s | 357.1 MB/s | 428.5 MB/s | 535.6 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 50,000 | 9,058 | 2,496,000 B | 4,292,608 B | 7,253,888 B | 2.32x | 415.1 MB/s | 830.1 MB/s | 996.2 MB/s | 1.22 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 50,000 | 4,506 | 2,496,000 B | 516,096 B | 3,652,496 B | 1.17x | 209.0 MB/s | 418.0 MB/s | 501.6 MB/s | 627.0 MB/s | ⚠️ **> 500 MB/s** |

#### Locality Distribution: **Mixed**

##### Scale: 1000 Entities | Locality: Mixed | Consumers: 1 (10,000 reqs, 550 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 10,000 | 10,000 | 0 B | 0 B | 624,000 B | 1.00x | 35.7 MB/s | 71.4 MB/s | 85.7 MB/s | 107.1 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 10,000 | 6,762 | 1,382,400 B | 6,135,808 B | 7,816,928 B | 12.53x | 447.3 MB/s | 894.6 MB/s | 1.05 GB/s | 1.31 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 10,000 | 6,006 | 1,382,400 B | 516,096 B | 2,665,296 B | 4.27x | 152.5 MB/s | 305.0 MB/s | 366.0 MB/s | 457.5 MB/s | ✅ Optimal |

##### Scale: 1000 Entities | Locality: Mixed | Consumers: 3 (30,000 reqs, 550 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 | 30,000 | 0 B | 0 B | 1,872,000 B | 1.00x | 107.1 MB/s | 214.2 MB/s | 257.1 MB/s | 321.4 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 30,000 | 16,190 | 1,382,400 B | 6,135,808 B | 8,414,368 B | 4.49x | 481.5 MB/s | 962.9 MB/s | 1.13 GB/s | 1.41 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 30,000 | 6,006 | 1,382,400 B | 516,096 B | 2,665,296 B | 1.42x | 152.5 MB/s | 305.0 MB/s | 366.0 MB/s | 457.5 MB/s | ✅ Optimal |

##### Scale: 1000 Entities | Locality: Mixed | Consumers: 5 (50,000 reqs, 550 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 50,000 | 50,000 | 0 B | 0 B | 3,120,000 B | 1.00x | 178.5 MB/s | 357.1 MB/s | 428.5 MB/s | 535.6 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 50,000 | 25,618 | 1,382,400 B | 6,135,808 B | 9,011,808 B | 2.89x | 515.7 MB/s | 1.01 GB/s | 1.21 GB/s | 1.51 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 50,000 | 6,006 | 1,382,400 B | 516,096 B | 2,665,296 B | 0.85x | 152.5 MB/s | 305.0 MB/s | 366.0 MB/s | 457.5 MB/s | ✅ Optimal |

#### Locality Distribution: **Scattered**

##### Scale: 1000 Entities | Locality: Scattered | Consumers: 1 (10,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 10,000 | 10,000 | 0 B | 0 B | 624,000 B | 1.00x | 35.7 MB/s | 71.4 MB/s | 85.7 MB/s | 107.1 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 10,000 | 9,172 | 0 B | 8,388,608 B | 8,833,216 B | 14.16x | 505.4 MB/s | 1010.9 MB/s | 1.18 GB/s | 1.48 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 10,000 | 8,006 | 0 B | 516,096 B | 1,452,496 B | 2.33x | 83.1 MB/s | 166.2 MB/s | 199.5 MB/s | 249.3 MB/s | ✅ Optimal |

##### Scale: 1000 Entities | Locality: Scattered | Consumers: 3 (30,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 | 30,000 | 0 B | 0 B | 1,872,000 B | 1.00x | 107.1 MB/s | 214.2 MB/s | 257.1 MB/s | 321.4 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 30,000 | 23,420 | 0 B | 8,388,608 B | 9,722,432 B | 5.19x | 556.3 MB/s | 1.09 GB/s | 1.30 GB/s | 1.63 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 30,000 | 8,006 | 0 B | 516,096 B | 1,452,496 B | 0.78x | 83.1 MB/s | 166.2 MB/s | 199.5 MB/s | 249.3 MB/s | ✅ Optimal |

##### Scale: 1000 Entities | Locality: Scattered | Consumers: 5 (50,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 50,000 | 50,000 | 0 B | 0 B | 3,120,000 B | 1.00x | 178.5 MB/s | 357.1 MB/s | 428.5 MB/s | 535.6 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 50,000 | 37,668 | 0 B | 8,388,608 B | 10,611,648 B | 3.40x | 607.2 MB/s | 1.19 GB/s | 1.42 GB/s | 1.78 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 50,000 | 8,006 | 0 B | 516,096 B | 1,452,496 B | 0.47x | 83.1 MB/s | 166.2 MB/s | 199.5 MB/s | 249.3 MB/s | ✅ Optimal |

### Scene: 1,000 Monsters + 1,000 Ground Items (2,000 Total Entities)

#### Locality Distribution: **Clustered**

##### Scale: 2000 Entities | Locality: Clustered | Consumers: 1 (20,000 reqs, 2000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 20,000 | 20,000 | 0 B | 0 B | 1,248,000 B | 1.00x | 71.4 MB/s | 142.8 MB/s | 171.4 MB/s | 214.2 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 20,000 | 12,868 | 4,992,000 B | 196,608 B | 5,835,968 B | 4.68x | 333.9 MB/s | 667.9 MB/s | 801.4 MB/s | 1001.8 MB/s | ⚠️ **> 500 MB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 20,000 | 9,000 | 4,992,000 B | 1,024,000 B | 7,294,000 B | 5.84x | 417.4 MB/s | 834.7 MB/s | 1001.7 MB/s | 1.22 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |

##### Scale: 2000 Entities | Locality: Clustered | Consumers: 3 (60,000 reqs, 2000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 60,000 | 60,000 | 0 B | 0 B | 3,744,000 B | 1.00x | 214.2 MB/s | 428.5 MB/s | 514.2 MB/s | 642.7 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 60,000 | 34,508 | 4,992,000 B | 196,608 B | 7,130,688 B | 1.90x | 408.0 MB/s | 816.0 MB/s | 979.3 MB/s | 1.20 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 60,000 | 9,000 | 4,992,000 B | 1,024,000 B | 7,294,000 B | 1.95x | 417.4 MB/s | 834.7 MB/s | 1001.7 MB/s | 1.22 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |

##### Scale: 2000 Entities | Locality: Clustered | Consumers: 5 (100,000 reqs, 2000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 100,000 | 100,000 | 0 B | 0 B | 6,240,000 B | 1.00x | 357.1 MB/s | 714.1 MB/s | 856.9 MB/s | 1.05 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 100,000 | 56,148 | 4,992,000 B | 196,608 B | 8,425,408 B | 1.35x | 482.1 MB/s | 964.2 MB/s | 1.13 GB/s | 1.41 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 100,000 | 9,000 | 4,992,000 B | 1,024,000 B | 7,294,000 B | 1.17x | 417.4 MB/s | 834.7 MB/s | 1001.7 MB/s | 1.22 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |

#### Locality Distribution: **Mixed**

##### Scale: 2000 Entities | Locality: Mixed | Consumers: 1 (20,000 reqs, 1100 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 20,000 | 20,000 | 0 B | 0 B | 1,248,000 B | 1.00x | 71.4 MB/s | 142.8 MB/s | 171.4 MB/s | 214.2 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 20,000 | 15,220 | 2,764,800 B | 3,883,008 B | 7,458,240 B | 5.98x | 426.8 MB/s | 853.5 MB/s | 1.00 GB/s | 1.25 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 20,000 | 12,000 | 2,764,800 B | 1,024,000 B | 5,319,600 B | 4.26x | 304.4 MB/s | 608.8 MB/s | 730.5 MB/s | 913.2 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 2000 Entities | Locality: Mixed | Consumers: 3 (60,000 reqs, 1100 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 60,000 | 60,000 | 0 B | 0 B | 3,744,000 B | 1.00x | 214.2 MB/s | 428.5 MB/s | 514.2 MB/s | 642.7 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 60,000 | 41,564 | 2,764,800 B | 3,883,008 B | 9,079,104 B | 2.42x | 519.5 MB/s | 1.01 GB/s | 1.22 GB/s | 1.52 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 60,000 | 12,000 | 2,764,800 B | 1,024,000 B | 5,319,600 B | 1.42x | 304.4 MB/s | 608.8 MB/s | 730.5 MB/s | 913.2 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 2000 Entities | Locality: Mixed | Consumers: 5 (100,000 reqs, 1100 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 100,000 | 100,000 | 0 B | 0 B | 6,240,000 B | 1.00x | 357.1 MB/s | 714.1 MB/s | 856.9 MB/s | 1.05 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 100,000 | 67,908 | 2,764,800 B | 3,883,008 B | 10,699,968 B | 1.71x | 612.3 MB/s | 1.20 GB/s | 1.43 GB/s | 1.79 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 100,000 | 12,000 | 2,764,800 B | 1,024,000 B | 5,319,600 B | 0.85x | 304.4 MB/s | 608.8 MB/s | 730.5 MB/s | 913.2 MB/s | ⚠️ **> 500 MB/s** |

#### Locality Distribution: **Scattered**

##### Scale: 2000 Entities | Locality: Scattered | Consumers: 1 (20,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 20,000 | 20,000 | 0 B | 0 B | 1,248,000 B | 1.00x | 71.4 MB/s | 142.8 MB/s | 171.4 MB/s | 214.2 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 20,000 | 19,172 | 0 B | 8,388,608 B | 9,457,216 B | 7.58x | 541.1 MB/s | 1.06 GB/s | 1.27 GB/s | 1.59 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 20,000 | 16,000 | 0 B | 1,024,000 B | 2,894,000 B | 2.32x | 165.6 MB/s | 331.2 MB/s | 397.4 MB/s | 496.8 MB/s | ✅ Optimal |

##### Scale: 2000 Entities | Locality: Scattered | Consumers: 3 (60,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 60,000 | 60,000 | 0 B | 0 B | 3,744,000 B | 1.00x | 214.2 MB/s | 428.5 MB/s | 514.2 MB/s | 642.7 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 60,000 | 53,420 | 0 B | 8,388,608 B | 11,594,432 B | 3.10x | 663.4 MB/s | 1.30 GB/s | 1.55 GB/s | 1.94 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 60,000 | 16,000 | 0 B | 1,024,000 B | 2,894,000 B | 0.77x | 165.6 MB/s | 331.2 MB/s | 397.4 MB/s | 496.8 MB/s | ✅ Optimal |

##### Scale: 2000 Entities | Locality: Scattered | Consumers: 5 (100,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 100,000 | 100,000 | 0 B | 0 B | 6,240,000 B | 1.00x | 357.1 MB/s | 714.1 MB/s | 856.9 MB/s | 1.05 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 100,000 | 87,668 | 0 B | 8,388,608 B | 13,731,648 B | 2.20x | 785.7 MB/s | 1.53 GB/s | 1.84 GB/s | 2.30 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 100,000 | 16,000 | 0 B | 1,024,000 B | 2,894,000 B | 0.46x | 165.6 MB/s | 331.2 MB/s | 397.4 MB/s | 496.8 MB/s | ✅ Optimal |

### Scene: 1,500 Monsters + 1,500 Ground Items (3,000 Total Entities)

#### Locality Distribution: **Clustered**

##### Scale: 3000 Entities | Locality: Clustered | Consumers: 1 (30,000 reqs, 3000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 | 30,000 | 0 B | 0 B | 1,872,000 B | 1.00x | 107.1 MB/s | 214.2 MB/s | 257.1 MB/s | 321.4 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 30,000 | 19,500 | 7,488,000 B | 0 B | 8,472,000 B | 4.53x | 484.8 MB/s | 969.5 MB/s | 1.14 GB/s | 1.42 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 30,000 | 13,506 | 7,488,000 B | 1,540,096 B | 10,946,496 B | 5.85x | 626.4 MB/s | 1.22 GB/s | 1.47 GB/s | 1.84 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

##### Scale: 3000 Entities | Locality: Clustered | Consumers: 3 (90,000 reqs, 3000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 90,000 | 90,000 | 0 B | 0 B | 5,616,000 B | 1.00x | 321.4 MB/s | 642.7 MB/s | 771.2 MB/s | 964.1 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 90,000 | 52,500 | 7,488,000 B | 0 B | 10,440,000 B | 1.86x | 597.4 MB/s | 1.17 GB/s | 1.40 GB/s | 1.75 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 90,000 | 13,506 | 7,488,000 B | 1,540,096 B | 10,946,496 B | 1.95x | 626.4 MB/s | 1.22 GB/s | 1.47 GB/s | 1.84 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

##### Scale: 3000 Entities | Locality: Clustered | Consumers: 5 (150,000 reqs, 3000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 150,000 | 150,000 | 0 B | 0 B | 9,360,000 B | 1.00x | 535.6 MB/s | 1.05 GB/s | 1.26 GB/s | 1.57 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 150,000 | 85,500 | 7,488,000 B | 0 B | 12,408,000 B | 1.33x | 710.0 MB/s | 1.39 GB/s | 1.66 GB/s | 2.08 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 150,000 | 13,506 | 7,488,000 B | 1,540,096 B | 10,946,496 B | 1.17x | 626.4 MB/s | 1.22 GB/s | 1.47 GB/s | 1.84 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

#### Locality Distribution: **Mixed**

##### Scale: 3000 Entities | Locality: Mixed | Consumers: 1 (30,000 reqs, 1650 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 | 30,000 | 0 B | 0 B | 1,872,000 B | 1.00x | 107.1 MB/s | 214.2 MB/s | 257.1 MB/s | 321.4 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 30,000 | 23,658 | 4,147,200 B | 1,630,208 B | 7,098,736 B | 3.79x | 406.2 MB/s | 812.4 MB/s | 974.9 MB/s | 1.19 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 30,000 | 18,006 | 4,147,200 B | 1,540,096 B | 7,984,896 B | 4.27x | 456.9 MB/s | 913.8 MB/s | 1.07 GB/s | 1.34 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |

##### Scale: 3000 Entities | Locality: Mixed | Consumers: 3 (90,000 reqs, 1650 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 90,000 | 90,000 | 0 B | 0 B | 5,616,000 B | 1.00x | 321.4 MB/s | 642.7 MB/s | 771.2 MB/s | 964.1 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 90,000 | 66,878 | 4,147,200 B | 1,630,208 B | 9,741,392 B | 1.73x | 557.4 MB/s | 1.09 GB/s | 1.31 GB/s | 1.63 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 90,000 | 18,006 | 4,147,200 B | 1,540,096 B | 7,984,896 B | 1.42x | 456.9 MB/s | 913.8 MB/s | 1.07 GB/s | 1.34 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |

##### Scale: 3000 Entities | Locality: Mixed | Consumers: 5 (150,000 reqs, 1650 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 150,000 | 150,000 | 0 B | 0 B | 9,360,000 B | 1.00x | 535.6 MB/s | 1.05 GB/s | 1.26 GB/s | 1.57 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 150,000 | 110,098 | 4,147,200 B | 1,630,208 B | 12,384,048 B | 1.32x | 708.6 MB/s | 1.38 GB/s | 1.66 GB/s | 2.08 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 150,000 | 18,006 | 4,147,200 B | 1,540,096 B | 7,984,896 B | 0.85x | 456.9 MB/s | 913.8 MB/s | 1.07 GB/s | 1.34 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |

#### Locality Distribution: **Scattered**

##### Scale: 3000 Entities | Locality: Scattered | Consumers: 1 (30,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 | 30,000 | 0 B | 0 B | 1,872,000 B | 1.00x | 107.1 MB/s | 214.2 MB/s | 257.1 MB/s | 321.4 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 30,000 | 29,172 | 0 B | 8,388,608 B | 10,081,216 B | 5.39x | 576.9 MB/s | 1.13 GB/s | 1.35 GB/s | 1.69 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 30,000 | 24,006 | 0 B | 1,540,096 B | 4,346,496 B | 2.32x | 248.7 MB/s | 497.4 MB/s | 596.9 MB/s | 746.1 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 3000 Entities | Locality: Scattered | Consumers: 3 (90,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 90,000 | 90,000 | 0 B | 0 B | 5,616,000 B | 1.00x | 321.4 MB/s | 642.7 MB/s | 771.2 MB/s | 964.1 MB/s | ⚠️ **> 500 MB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 90,000 | 83,420 | 0 B | 8,388,608 B | 13,466,432 B | 2.40x | 770.6 MB/s | 1.50 GB/s | 1.81 GB/s | 2.26 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 90,000 | 24,006 | 0 B | 1,540,096 B | 4,346,496 B | 0.77x | 248.7 MB/s | 497.4 MB/s | 596.9 MB/s | 746.1 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 3000 Entities | Locality: Scattered | Consumers: 5 (150,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 150,000 | 150,000 | 0 B | 0 B | 9,360,000 B | 1.00x | 535.6 MB/s | 1.05 GB/s | 1.26 GB/s | 1.57 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 150,000 | 137,668 | 0 B | 8,388,608 B | 16,851,648 B | 1.80x | 964.3 MB/s | 1.88 GB/s | 2.26 GB/s | 2.82 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 150,000 | 24,006 | 0 B | 1,540,096 B | 4,346,496 B | 0.46x | 248.7 MB/s | 497.4 MB/s | 596.9 MB/s | 746.1 MB/s | ⚠️ **> 500 MB/s** |

### Scene: 2,000 Monsters + 2,000 Ground Items (4,000 Total Entities)

#### Locality Distribution: **Clustered**

##### Scale: 4000 Entities | Locality: Clustered | Consumers: 1 (40,000 reqs, 4000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 40,000 | 40,000 | 0 B | 0 B | 2,496,000 B | 1.00x | 142.8 MB/s | 285.6 MB/s | 342.8 MB/s | 428.5 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 40,000 | 26,000 | 9,984,000 B | 0 B | 11,296,000 B | 4.53x | 646.4 MB/s | 1.26 GB/s | 1.51 GB/s | 1.89 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 40,000 | 18,000 | 9,984,000 B | 2,048,000 B | 14,588,000 B | 5.84x | 834.7 MB/s | 1.63 GB/s | 1.96 GB/s | 2.45 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

##### Scale: 4000 Entities | Locality: Clustered | Consumers: 3 (120,000 reqs, 4000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 120,000 | 120,000 | 0 B | 0 B | 7,488,000 B | 1.00x | 428.5 MB/s | 856.9 MB/s | 1.00 GB/s | 1.26 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 120,000 | 70,000 | 9,984,000 B | 0 B | 13,920,000 B | 1.86x | 796.5 MB/s | 1.56 GB/s | 1.87 GB/s | 2.33 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 120,000 | 18,000 | 9,984,000 B | 2,048,000 B | 14,588,000 B | 1.95x | 834.7 MB/s | 1.63 GB/s | 1.96 GB/s | 2.45 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

##### Scale: 4000 Entities | Locality: Clustered | Consumers: 5 (200,000 reqs, 4000 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 200,000 | 200,000 | 0 B | 0 B | 12,480,000 B | 1.00x | 714.1 MB/s | 1.39 GB/s | 1.67 GB/s | 2.09 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 200,000 | 114,000 | 9,984,000 B | 0 B | 16,544,000 B | 1.33x | 946.7 MB/s | 1.85 GB/s | 2.22 GB/s | 2.77 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 200,000 | 18,000 | 9,984,000 B | 2,048,000 B | 14,588,000 B | 1.17x | 834.7 MB/s | 1.63 GB/s | 1.96 GB/s | 2.45 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

#### Locality Distribution: **Mixed**

##### Scale: 4000 Entities | Locality: Mixed | Consumers: 1 (40,000 reqs, 2200 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 40,000 | 40,000 | 0 B | 0 B | 2,496,000 B | 1.00x | 142.8 MB/s | 285.6 MB/s | 342.8 MB/s | 428.5 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 40,000 | 32,000 | 5,529,600 B | 0 B | 7,347,200 B | 2.94x | 420.4 MB/s | 840.8 MB/s | 1009.0 MB/s | 1.23 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 40,000 | 24,000 | 5,529,600 B | 2,048,000 B | 10,639,200 B | 4.26x | 608.8 MB/s | 1.19 GB/s | 1.43 GB/s | 1.78 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

##### Scale: 4000 Entities | Locality: Mixed | Consumers: 3 (120,000 reqs, 2200 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 120,000 | 120,000 | 0 B | 0 B | 7,488,000 B | 1.00x | 428.5 MB/s | 856.9 MB/s | 1.00 GB/s | 1.26 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 120,000 | 91,600 | 5,529,600 B | 0 B | 10,982,400 B | 1.47x | 628.4 MB/s | 1.23 GB/s | 1.47 GB/s | 1.84 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 120,000 | 24,000 | 5,529,600 B | 2,048,000 B | 10,639,200 B | 1.42x | 608.8 MB/s | 1.19 GB/s | 1.43 GB/s | 1.78 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

##### Scale: 4000 Entities | Locality: Mixed | Consumers: 5 (200,000 reqs, 2200 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 200,000 | 200,000 | 0 B | 0 B | 12,480,000 B | 1.00x | 714.1 MB/s | 1.39 GB/s | 1.67 GB/s | 2.09 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 200,000 | 151,200 | 5,529,600 B | 0 B | 14,617,600 B | 1.17x | 836.4 MB/s | 1.63 GB/s | 1.96 GB/s | 2.45 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 200,000 | 24,000 | 5,529,600 B | 2,048,000 B | 10,639,200 B | 0.85x | 608.8 MB/s | 1.19 GB/s | 1.43 GB/s | 1.78 GB/s | 💥 **EXCEEDS 1.5 GB/s** |

#### Locality Distribution: **Scattered**

##### Scale: 4000 Entities | Locality: Scattered | Consumers: 1 (40,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 40,000 | 40,000 | 0 B | 0 B | 2,496,000 B | 1.00x | 142.8 MB/s | 285.6 MB/s | 342.8 MB/s | 428.5 MB/s | ✅ Optimal |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 40,000 | 39,172 | 0 B | 8,388,608 B | 10,705,216 B | 4.29x | 612.6 MB/s | 1.20 GB/s | 1.44 GB/s | 1.79 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 40,000 | 32,000 | 0 B | 2,048,000 B | 5,788,000 B | 2.32x | 331.2 MB/s | 662.4 MB/s | 794.9 MB/s | 993.6 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 4000 Entities | Locality: Scattered | Consumers: 3 (120,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 120,000 | 120,000 | 0 B | 0 B | 7,488,000 B | 1.00x | 428.5 MB/s | 856.9 MB/s | 1.00 GB/s | 1.26 GB/s | 🚨 **EXCEEDS 1.0 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 120,000 | 113,420 | 0 B | 8,388,608 B | 15,338,432 B | 2.05x | 877.7 MB/s | 1.71 GB/s | 2.06 GB/s | 2.57 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 120,000 | 32,000 | 0 B | 2,048,000 B | 5,788,000 B | 0.77x | 331.2 MB/s | 662.4 MB/s | 794.9 MB/s | 993.6 MB/s | ⚠️ **> 500 MB/s** |

##### Scale: 4000 Entities | Locality: Scattered | Consumers: 5 (200,000 reqs, 0 planned ranges)
| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 200,000 | 200,000 | 0 B | 0 B | 12,480,000 B | 1.00x | 714.1 MB/s | 1.39 GB/s | 1.67 GB/s | 2.09 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Current NewMemoryRead (4KB/8KB + Production Planned Ranges)** | 200,000 | 187,668 | 0 B | 8,388,608 B | 19,971,648 B | 1.60x | 1.12 GB/s | 2.23 GB/s | 2.68 GB/s | 3.35 GB/s | 💥 **EXCEEDS 1.5 GB/s** |
| **Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)** | 200,000 | 32,000 | 0 B | 2,048,000 B | 5,788,000 B | 0.46x | 331.2 MB/s | 662.4 MB/s | 794.9 MB/s | 993.6 MB/s | ⚠️ **> 500 MB/s** |

## 3. Traffic Category Breakdown (1500 Monsters + 1500 Ground Items Mixed Scene)

| Workload Category | Requests / Ranges Count | Logical / Planned Bytes | Category Traffic Share |
| :--- | :---: | :---: | :---: |
| **Entity / Header Structs** | 6,000 | 336,000 B | 5.6% |
| **Component Snapshot / Planned Ranges** | 1,650 | 4,147,200 B | 68.9% |
| **Render / Position Components** | 7,500 | 432,000 B | 7.2% |
| **Actor / Life / Monster State** | 6,000 | 480,000 B | 8.0% |
| **Ground Item & Inner Item Data** | 7,500 | 480,000 B | 8.0% |
| **Strings / Path / Metadata** | 3,000 | 144,000 B | 2.4% |

## 4. Cost Model Sensitivity Sweep
Evaluation on Mixed Realistic Workload (1000 Monsters + 1000 Ground Items Mixed with 3 Consumers):

| CallCost Ratio | Legacy Exact-Read | Current NewMemoryRead | Proposed Hybrid V2 | Optimal Policy |
| :---: | :---: | :---: | :---: | :---: |
| **250** | 18,744,000 | 19,470,104 | 8,319,600 | **Hybrid V2** |
| **500** | 33,744,000 | 29,861,104 | 11,319,600 | **Hybrid V2** |
| **1,000** | 63,744,000 | 50,643,104 | 17,319,600 | **Hybrid V2** |
| **2,000** | 123,744,000 | 92,207,104 | 29,319,600 | **Hybrid V2** |
| **5,000** | 303,744,000 | 216,899,104 | 65,319,600 | **Hybrid V2** |

## 5. Pareto-Optimal Configuration Analysis
Comparison of Candidate Configurations on 1000 Monster + 1000 Ground Item Mixed Scene (3 Consumers):

| Candidate Configuration | Native Calls | Fetched Bytes | Traffic Ratio | Cost (CallCost=1000) | Pareto-Optimal Status |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 60,000 | 3,744,000 B | 1.00x | 63,744,000 | Lowest Fetched / Highest Calls |
| **Current NewMemoryRead (4KB/8KB + Planned Ranges)** | 41,564 | 9,079,104 B | 2.42x | 50,643,104 | Lowest Calls / Highest Wasted |
| **Hybrid V2 (Exact + 128B + 512B + 4KB)** | 12,000 | 5,319,600 B | 1.42x | 17,319,600 | Pareto-Optimal (Balanced) |
| **Hybrid V2 (Exact + 64B + 256B + 4KB)** | 12,500 | 6,831,600 B | 1.82x | 19,331,600 | Pareto-Optimal (Balanced) |
| **Hybrid V2 (Exact + 256B + 1024B + 4KB)** | 11,250 | 5,623,600 B | 1.50x | 16,873,600 | Pareto-Optimal (Balanced) |
