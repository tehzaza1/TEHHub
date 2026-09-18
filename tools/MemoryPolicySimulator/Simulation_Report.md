# Offline Memory Read Policy Simulation Report
Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead vs. Proposed Hybrid V2

Cost Model: `EstimatedCost = (NativeCalls * 1000) + (FetchedBytes * 1)`

## 1. Skill Workload (Actor + ActiveSkills + Cooldowns)

### Scale: 10 items (134 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 1,156 B | 134 | 1,156 B | 1.00x | 0 B | 135,156 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 1,156 B | 5 | 20,480 B | 17.72x | 19,324 B | 25,480 |
| **Proposed Hybrid V2** | 1,156 B | 10 | 21,760 B | 18.82x | 20,604 B | 31,760 |

### Scale: 100 items (1304 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 10,516 B | 1,304 | 10,516 B | 1.00x | 0 B | 1,314,516 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 10,516 B | 20 | 81,920 B | 7.79x | 71,404 B | 101,920 |
| **Proposed Hybrid V2** | 10,516 B | 40 | 87,040 B | 8.28x | 76,524 B | 127,040 |

### Scale: 500 items (6504 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 52,116 B | 6,504 | 52,116 B | 1.00x | 0 B | 6,556,116 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 52,116 B | 88 | 360,448 B | 6.92x | 308,332 B | 448,448 |
| **Proposed Hybrid V2** | 52,116 B | 176 | 382,976 B | 7.35x | 330,860 B | 558,976 |

### Scale: 1000 items (13004 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 104,116 B | 13,004 | 104,116 B | 1.00x | 0 B | 13,108,116 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 104,116 B | 174 | 712,704 B | 6.85x | 608,588 B | 886,704 |
| **Proposed Hybrid V2** | 104,116 B | 348 | 757,248 B | 7.27x | 653,132 B | 1,105,248 |

## 2. Entity / Component Workload (Dense Component Clusters)

### Scale: 10 items (180 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 3,000 B | 180 | 3,000 B | 1.00x | 0 B | 183,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 3,000 B | 15 | 61,440 B | 20.48x | 58,440 B | 76,440 |
| **Proposed Hybrid V2** | 3,000 B | 20 | 24,320 B | 8.11x | 21,320 B | 44,320 |

### Scale: 100 items (1800 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 30,000 B | 1,800 | 30,000 B | 1.00x | 0 B | 1,830,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 30,000 B | 150 | 614,400 B | 20.48x | 584,400 B | 764,400 |
| **Proposed Hybrid V2** | 30,000 B | 200 | 243,200 B | 8.11x | 213,200 B | 443,200 |

### Scale: 500 items (9000 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 150,000 B | 9,000 | 150,000 B | 1.00x | 0 B | 9,150,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 150,000 B | 750 | 3,072,000 B | 20.48x | 2,922,000 B | 3,822,000 |
| **Proposed Hybrid V2** | 150,000 B | 1,000 | 1,216,000 B | 8.11x | 1,066,000 B | 2,216,000 |

### Scale: 1000 items (18000 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 300,000 B | 18,000 | 300,000 B | 1.00x | 0 B | 18,300,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 300,000 B | 1,500 | 6,144,000 B | 20.48x | 5,844,000 B | 7,644,000 |
| **Proposed Hybrid V2** | 300,000 B | 2,000 | 2,432,000 B | 8.11x | 2,132,000 B | 4,432,000 |

## 3. UI-Tree Workload (Hierarchical UI Traversal & String Reads)

### Scale: 10 items (71 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 1,640 B | 71 | 1,640 B | 1.00x | 0 B | 72,640 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 1,640 B | 4 | 16,384 B | 9.99x | 14,744 B | 20,384 |
| **Proposed Hybrid V2** | 1,640 B | 8 | 17,408 B | 10.61x | 15,768 B | 25,408 |

### Scale: 100 items (671 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 15,800 B | 671 | 15,800 B | 1.00x | 0 B | 686,800 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 15,800 B | 26 | 106,496 B | 6.74x | 90,696 B | 132,496 |
| **Proposed Hybrid V2** | 15,800 B | 52 | 113,152 B | 7.16x | 97,352 B | 165,152 |

### Scale: 500 items (3337 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 78,720 B | 3,337 | 78,720 B | 1.00x | 0 B | 3,415,720 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 78,720 B | 126 | 516,096 B | 6.56x | 437,376 B | 642,096 |
| **Proposed Hybrid V2** | 78,720 B | 252 | 548,352 B | 6.97x | 469,632 B | 800,352 |

### Scale: 1000 items (6671 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 157,400 B | 6,671 | 157,400 B | 1.00x | 0 B | 6,828,400 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 157,400 B | 251 | 1,028,096 B | 6.53x | 870,696 B | 1,279,096 |
| **Proposed Hybrid V2** | 157,400 B | 502 | 1,092,352 B | 6.94x | 934,952 B | 1,594,352 |

## 4. Worst-Case Scattered Workload (1 Scalar Read per 4KB Page)

### Scale: 10 items (10 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 80 B | 10 | 80 B | 1.00x | 0 B | 10,080 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 80 B | 10 | 40,960 B | 512.00x | 40,880 B | 50,960 |
| **Proposed Hybrid V2** | 80 B | 10 | 2,560 B | 32.00x | 2,480 B | 12,560 |

### Scale: 100 items (100 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 800 B | 100 | 800 B | 1.00x | 0 B | 100,800 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 800 B | 100 | 409,600 B | 512.00x | 408,800 B | 509,600 |
| **Proposed Hybrid V2** | 800 B | 100 | 25,600 B | 32.00x | 24,800 B | 125,600 |

### Scale: 500 items (500 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 4,000 B | 500 | 4,000 B | 1.00x | 0 B | 504,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 4,000 B | 500 | 2,048,000 B | 512.00x | 2,044,000 B | 2,548,000 |
| **Proposed Hybrid V2** | 4,000 B | 500 | 128,000 B | 32.00x | 124,000 B | 628,000 |

### Scale: 1000 items (1000 requests)
| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **Legacy Exact-Read** | 8,000 B | 1,000 | 8,000 B | 1.00x | 0 B | 1,008,000 |
| **Current NewMemoryRead (4KB/8KB Page Cache)** | 8,000 B | 1,000 | 4,096,000 B | 512.00x | 4,088,000 B | 5,096,000 |
| **Proposed Hybrid V2** | 8,000 B | 1,000 | 256,000 B | 32.00x | 248,000 B | 1,256,000 |

## Hybrid V2 Parameter Sensitivity & Tuning Analysis
Evaluation on Mixed Realistic Workload (100 Skills + 100 Entities + 100 UI elements):

| Compact Block Size | Promotion Threshold | Native Calls | Fetched Bytes | Amplification | Estimated Cost |
| :---: | :---: | :---: | :---: | :---: | :---: |
| 64 B | 1 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 64 B | 2 | 392 | 412,160 B | 7.22x | 804,160 |
| 64 B | 3 | 487 | 414,208 B | 7.25x | 901,208 |
| 64 B | 4 | 581 | 416,192 B | 7.29x | 997,192 |
| 128 B | 1 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 128 B | 2 | 392 | 431,104 B | 7.55x | 823,104 |
| 128 B | 3 | 487 | 439,296 B | 7.69x | 926,296 |
| 128 B | 4 | 581 | 447,360 B | 7.83x | 1,028,360 |
| 256 B | 1 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 256 B | 2 | 392 | 468,992 B | 8.21x | 860,992 |
| 256 B | 3 | 486 | 485,376 B | 8.50x | 971,376 |
| 256 B | 4 | 579 | 505,344 B | 8.85x | 1,084,344 |
| 512 B | 1 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 512 B | 2 | 391 | 540,672 B | 9.47x | 931,672 |
| 512 B | 3 | 485 | 585,216 B | 10.25x | 1,070,216 |
| 512 B | 4 | 578 | 629,248 B | 11.02x | 1,207,248 |
| 1024 B | 1 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 1024 B | 2 | 391 | 692,224 B | 12.12x | 1,083,224 |
| 1024 B | 3 | 483 | 777,216 B | 13.61x | 1,260,216 |
| 1024 B | 4 | 574 | 867,328 B | 15.19x | 1,441,328 |
| 4096 B | 1 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 4096 B | 2 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 4096 B | 3 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
| 4096 B | 4 | 296 | 1,212,416 B | 21.23x | 1,508,416 |
