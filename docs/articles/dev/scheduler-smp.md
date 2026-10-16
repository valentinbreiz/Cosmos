# Multi-core scheduling (SMP): design proposal

This is a proposal, written against the gen3 tree at commit `db2c034` and not implemented. It inventories what pins the kernel to one CPU today, compares the designs that would lift that, and recommends one, in phases that each leave the tree shippable. Read [Scheduler](scheduler.md) first: the names below (`SchedulerManager`, `PerCpuState`, `IScheduler`, the park protocol, the staged switch) are its.

## Summary

The recommendation is per-CPU scheduling on the shape `IScheduler` already has: every CPU runs its own tick, its own run queue under its own lock, and picks its own next thread; a wake aimed at another CPU goes through that CPU's queue lock and an inter-processor interrupt, so the woken thread runs within microseconds rather than at that CPU's next tick. Affinity follows Linux and Windows: each thread carries a hard CPU set and a soft ideal CPU, a new thread inherits its creator's set, and the kernel keeps a default set that the main thread starts from. The garbage collector stays stop-the-world: a collection stops the other CPUs with an interrupt, and the frame that interrupt saves is a `ThreadContext`, the same structure a preemption saves, so a thread running on another CPU is scanned exactly as a parked thread is scanned today.

The suggestion that started this proposal was to dedicate CPU 0 to the garbage collector and the scheduler and run the main kernel thread, with load balancing, on the rest. The segmentation is kept, in the form it can take with this runtime. CPU 0 becomes the housekeeping CPU: every device interrupt stays routed to it, the driver kit's threads stay on it, the software timers, the alarms and the debug snapshots run there, and the default affinity of the main thread and of everything it creates is the other CPUs. That split is what lets SMP ship without first auditing the driver stack, whose exclusion against its own interrupt handlers is often an interrupt mask alone, correct on one CPU and meaningless across two. What the proposal does not do is make CPU 0 run the scheduler or the collector on behalf of the others: each CPU scheduling itself is both simpler and faster, and the collector gains nothing from a dedicated core until it is concurrent, which it is not. [Section 4](#4-about-cpu-0-for-the-gc-and-the-scheduler) gives the reasons.

## 1. What runs on one CPU today

The scheduler article says the structures are per-CPU-shaped and lists three things SMP needs: per-CPU staging, a real CPU id, and a balancing call site. The full list is longer. Everything below was read from the tree; each row is a thing that is correct on one CPU and wrong on two.

| Area | What assumes one CPU | Where |
|------|----------------------|-------|
| Identity | `GetCurrentCpuId()` returns 0, and `CurrentCpuState`, thread statics, the TLAB lookup, the stack bounds and `SystemNative_SchedGetCpu` all go through it | `Scheduler/SchedulerManager.cs:440`, `Runtime/Thread.cs`, `GarbageCollector.Tlab.cs`, `Bridge/Interop/libSystemNative.cs` |
| Switch staging | The staged target stack pointer, the new-thread flag, the restore scratch and the stashed error code are single native globals that every interrupt exit on every CPU reads | `Native.X64/CPU/Interrupts.s` (`_context_switch_target_rsp`, `_context_switch_is_new_thread`, `_temp_is_new_thread`, `_irq_last_error_code`), `Native.ARM64/CPU/ContextSwitch.s` |
| CPU id in handlers | The x64 tick and dispatcher pass the raw APIC id as the CPU index; ARM64 passes 0 | `Core.X64/Cpu/LocalApic.cs` (`HandleTimerInterrupt`), `Core.X64/Cpu/X64InterruptController.cs` (`Dispatch`), `Core.ARM64/Cpu/ARM64InterruptController.cs` (`RunPendingReschedule`) |
| Mutual exclusion | Masking interrupts is the only lock in `RegisterThread`, `CreateThread`, `ReadyThread`, `BlockThread`, `MarkSleeping`, `ExitThread` and `GetBusyCpuTimeNs`; `PerCpuState._lock` guards only the dead `Schedule` path and `SetPriority`; `AllocateThreadId` is a bare increment | `Scheduler/SchedulerManager.cs` |
| Remote wakes | `ReadyThread(thread.CpuId, ...)` from `Mutex`, `ConditionVariable` and `InterruptEvent` mutates whichever CPU's queue the thread belongs to, and sets that CPU's `_needReschedule` with nothing to make that CPU look at it | `Scheduler/Mutex.cs:267`, `ConditionVariable.cs:172`, `InterruptEvent.cs:359` |
| Sleepers | Every CPU's tick scans the whole registry and wakes every CPU's sleepers | `SchedulerManager.CheckSleepingThreads` |
| Idle threads | The idle thread is the boot context; `BootStack.Top` is one global, and the GC skips any other `Running` thread on the assumption that it never ran | `Runtime/BootStack.cs`, `GarbageCollector.Mark.cs:158` |
| Placement | New threads are created with `CpuId = 0`; `SelectCpu`, `Balance` and `OnThreadMigrate` have no mechanism-side caller; `Pinned` is the only affinity and only idle threads carry it | `libSystemNative.cs`, `Scheduler/KernelThread.cs`, `Stride/StrideScheduler.cs:326` |
| Policy swap | `SetScheduler` takes `s_globalLock`, the one spinlock the mechanism uses, and rebuilds every CPU's run structure under it, allocating as it goes; the other CPUs' ticks would spin on that lock with interrupts masked | `SchedulerManager.cs:24,137` |
| Collector | `Collect` masks local interrupts and treats that as the world stopped; the TLAB refill, the free lists, the segments, the pinned heap and `ReturnAllAllocContexts` have no lock; `s_totalAllocatedBytes` is a shared counter bumped on the fast path | `GarbageCollector.cs`, `GarbageCollector.Alloc.cs`, `GarbageCollector.Tlab.cs` |
| Unmanaged heap | `Heap.Alloc` and `Heap.Free`, which back thread stacks through `MemoryOp.Alloc`, mask interrupts and take no lock; `PageAllocator` already has an IRQ-safe `s_ratLock` | `Memory/Heap/Heap.cs`, `Memory/PageAllocator.cs:92` |
| GC handles | `GCHandleSegment.TryAllocate` is a lock-free CAS loop, but the segment fallback and `ClearWeakHandles` have no lock | `GarbageCollector/GCHandleManager.cs` |
| Atomics | `RhpCheckedLockCmpXchg` and `RhpCheckedXchg`, which back `Interlocked.CompareExchange` and `Exchange` on references and so CoreLib's `Lock`, `ThreadPool` and `ConcurrentQueue`, are plain read-then-write C# | `Runtime/Stdllib.cs:135,168` |
| Exception handling | The ExInfo chain head is one native global documented as "single-threaded kernel, no TLS needed", and the recursion guard and the running-funclet records are statics | `Native.X64/CPU/ExceptionHandling.s:9`, `Runtime/ExceptionHandling/ExceptionHelper.cs:33-64` |
| Dispatch cache | `Remember` writes an entry under an interrupt mask and says so: "Cosmos runs on one CPU" | `Runtime/CachedInterfaceDispatch.cs:337` |
| Serial | No lock around a write; two CPUs logging interleave bytes | `IO/Serial.cs` |
| Interrupt routing | Every line and every message is aimed at the boot CPU's APIC id, which `X64MsiBinder` notes as waiting for an index-to-APIC-id map | `Core.X64/Cpu/ApicManager.cs`, `X64MsiBinder.cs:141`, `HAL/DriverKit/Buses/Pci/PciMessageTable.cs:55` |
| IPIs | x64 can only send a self-IPI; the GICv3 SGI path targets CPU 0 by assumption | `LocalApic.SendSelfIpi`, `Core.ARM64/Cpu/GICv3.cs:724` |
| Per-CPU hardware | The LAPIC, the IDT load, the SIMD enable, the GIC redistributor and CPU interface, the exception vectors and the generic timer are all initialized once, on the boot CPU | `ApicManager`, `Idt`, `kmain.c`, `GICv3.Initialize`, `GenericTimer` |
| CPU count | x64 counts MADT entries; ARM64 returns 1; `RhGetProcessCpuCount` returns 1, so `Environment.ProcessorCount` and the thread pool's size are 1 | `HAL.X64/X64PlatformInitializer.cs:196`, `HAL.ARM64/ARM64PlatformInitializer.cs:440`, `Runtime/Stdllib.cs:181` |
| Boot | No Limine MP request; application processors are left parked by the bootloader | `Boot.Limine/Limine.cs` |

Three of these are bugs on one CPU as well and are worth fixing first regardless of SMP: the non-atomic reference exchanges, the global exception-handling state (two threads throwing across a preemption share one chain head), and the interrupt-masked critical sections that allocate.

Two things are in better shape than they look. `SpinLock`, `Mutex`, `ConditionVariable`, `InterruptEvent` and the driver kit's `DeviceLock` already take real spinlocks through `AcquireIrqSafe`, so their exclusion holds across CPUs once the scheduler calls underneath them do. And `PageAllocator` only hands out `Usable` memory map regions, never bootloader-reclaimable ones, which is where Limine parks the application processors; nothing has to change there before they are started.

## 2. The designs considered

| | A. One global queue, one scheduler lock | B. Per-CPU queues and locks, each CPU schedules itself | C. One lock around the whole kernel | D. CPU 0 schedules for all, the others only run |
|---|---|---|---|---|
| Shape | Every CPU's tick takes the one lock and pops the one Stride queue, skipping threads whose affinity excludes it | What `IScheduler` was drawn for: `PerCpuState` per CPU, `SelectCpu` at wake, `Balance` when idle | Every entry into scheduler, allocator and driver code serializes on one lock | CPU 0's tick decides for every CPU and sends each an IPI telling it what to run; blocking primitives message CPU 0 |
| Fairness | Exact, one virtual clock | Per CPU, as in Linux; balancing evens it out | Exact | Exact |
| Cost per switch | One contended lock and a cache line bouncing between every CPU on every tick and wake | The CPU's own lock, uncontended except by a remote wake | A lock everything waits on; allocation is everywhere in a managed kernel, so little runs in parallel | An IPI round trip on every switch and every park; CPU 0 does no useful work and is the bottleneck on the two- to four-CPU machines the tests use |
| Change to the seam | `SelectCpu` and `Balance` stay dead | `SelectCpu` and `Balance` gain affinity parameters and callers | None | A different interface |
| Verdict | Workable up to a handful of CPUs, but it abandons the per-CPU design already paid for | Recommended | Rejected | Rejected; see section 4 |

B is the recommendation. It is the Linux and Windows shape, it is what `PerCpuState`, `SchedulerData`, `SelectCpu`, `Balance` and `OnThreadMigrate` were written for, and its locks are uncontended in the common case. Its one real cost over A, per-CPU fairness, is the cost every production scheduler accepts.

## 3. The design

### 3.1 Identity: the per-CPU block

Every CPU gets a small native block, and a register that always points at it: `IA32_GS_BASE` on x64, written once per CPU with `wrmsr` (there is no user mode, so `swapgs` never enters the picture), and `TPIDR_EL1` on ARM64. Neither register is used by ILC's code generation in this kernel, since thread statics go through `RhGetThreadStaticStorage` rather than a TLS register.

The block holds what the assembly stubs and the hot managed paths need to find in one load:

| Field | Replaces |
|-------|----------|
| `Self`, `CpuIndex`, `ApicId` or `Mpidr` | `GetCurrentCpuId() => 0` and the APIC-id-as-index shortcut |
| `SwitchTargetSp`, `SwitchIsNewThread`, `TempIsNewThread` | the three staging globals |
| `LastErrorCode` | `_irq_last_error_code` |
| `CurrentExInfoHead` | `__cosmos_exinfo_stack_head`, a pointer to the current thread's chain head (section 3.7) |
| `BootStackTop`, `BootStackSize` | `BootStack` for this CPU's idle thread |
| `Online`, `StoppedForGc` | new |

The IRQ stubs read `gs:[SwitchTargetSp]` (on ARM64, `mrs x11, tpidr_el1` then a load) where they read the global today; the three staging imports in `ContextSwitchNative` keep their signatures and write the current CPU's slots. `GetCurrentCpuId()` becomes a two-instruction native helper behind a `[SuppressGCTransition]` import, which is cheap enough for a path taken on every thread-static access and every allocation; if it ever shows in a profile it can become an ILC intrinsic later. Block zero is the boot CPU, blocks one onward follow Limine's CPU list, and the table of APIC ids or MPIDRs per index is what IPIs and the MSI binder look up.

### 3.2 Bringing the application processors online

The bootloader does the hard part. A Limine MP request (a `LimineID` of `0x95a67b819a1b857e, 0xa0b61b723b6a73e0`, the header's `LIMINE_MP_REQUEST_ID`, added to `Cosmos.Kernel.Boot.Limine` in the same preinitialized-struct pattern as the other requests) answers with the CPU count, the boot CPU's APIC id or MPIDR, and one `limine_mp_info` per CPU. Writing an address into a CPU's `goto_address` field starts it there in the same state the boot CPU had at `kmain`: long mode or EL1, the bootloader's page tables (which the kernel keeps and only adds to, so every CPU shares one address space), interrupts masked, a stack of its own, and a pointer to its `limine_mp_info` in the first argument register. The stack is as large as the Stack Size request asked for, 1 MiB here, since Limine's loader allocates every application processor's stack with the same size it gave the boot CPU (`common/protos/limine.c` in the v10 tree). The kernel puts the CPU's per-CPU block address in the `extra_argument` field before the write. The request's flag bit 0 asks for x2APIC mode; the proposal leaves it clear, which keeps the Local APIC in the xAPIC mode the existing code drives. Limine's list, not the MADT, is the truth about how many CPUs exist and which one booted; the MADT keeps its role for the I/O APIC and the overrides.

Each application processor enters a native trampoline that does what no managed code can do before identity exists: load the per-CPU register, enable SIMD, and on x64 load the IDT (the same table; `lidt` is per CPU) or on ARM64 set `VBAR_EL1` and clear `SCTLR_EL1.A`, then call a managed `__managed__ap_main`. From there it is C#: the idle thread the boot CPU already created for this index is this CPU's current thread, so thread statics, allocation and class constructors work; the CPU records its boot stack bounds on that idle thread (so `RhGetCurrentThreadStackBounds` and the collector see a real stack, and `BootStack` stops being special), initializes its own interrupt hardware through a new `IInterruptController.InitializeCurrentCpu` (x64: spurious vector and enable, task priority, masked LVTs; ARM64: its own redistributor found by MPIDR, the CPU interface system registers, the timer PPI and the reschedule SGI, which are banked per CPU), starts its own scheduler tick through the platform initializer (the LAPIC timer is per CPU and reuses the boot CPU's calibration; `CNTP_*` is banked), marks itself online, enables interrupts and halts in a loop. Its first tick finds nothing queued and keeps halting; the first thread placed on it arrives by a reschedule IPI.

Where this happens is the end of `LibraryInitializer.InitializeLibrary`, after the policy is installed and the boot CPU's timer is running, and before the ring's own initializer brings the drivers up. Until placement is enabled (section 3.4), an online application processor runs only its idle thread, so every existing test suite runs unchanged on a four-CPU machine; that is the regression guard for the bring-up itself.

### 3.3 The scheduler mechanism under SMP

The mechanism keeps its shape and gains locks, a remote wake, and a migration primitive.

`PerCpuState._lock` becomes the queue lock: an IRQ-safe spinlock taken around every policy hook that touches that CPU's run structure, around `CurrentThread` and around `_needReschedule`. The owning CPU takes it from its tick and its interrupt exits; a remote CPU takes it to wake a thread onto this CPU or to pull one off it. A registry lock guards `s_allThreads`, the counts and the thread id; `s_globalLock`, which today guards only `SetScheduler`, becomes it. The order is queue locks in ascending CPU index, then the registry lock, then the heap lock; two queue locks are held at once only by a migration. One rule governs all of them, and it is already the rule for the tick path: nothing allocates while holding a spinlock. Section 3.6 says why that rule is what keeps a stop-the-world collection from deadlocking.

`State`, `_needReschedule` and the online mask are read on one CPU and written on another, so they are read and written through `Volatile`, and every handshake is an acquire-release pair. On x64 this costs nothing extra; on ARM64 it is the difference between a kernel that works and one that hangs rarely and unreproducibly.

`ReadyThread` first asks the policy where the thread goes (`SelectCpu`, under the thread's affinity; section 3.4), takes that CPU's queue lock, inserts, sets its `_needReschedule`, and if that CPU is not the caller's, sends it the reschedule interrupt: on x64 the existing vector `0xF0` by fixed delivery to its APIC id (the ICR high and low writes run with interrupts masked after waiting for the delivery-status bit, since a handler on the same CPU may be sending too), on ARM64 the existing SGI 8 with the target's affinity in `ICC_SGI1R_EL1`. The target's interrupt exit runs `ReschedulePendingFromIrq` and switches; a target halted in its idle loop wakes on the interrupt. Nothing changes in the park protocol: `Park` still raises a self-interrupt and spins on its own state, which is per CPU already.

Each CPU's tick wakes only the sleepers whose `CpuId` is its own, a filter on the registry scan it already does; a per-CPU sleeper list is a later optimization. A sleeper's wake is a `ReadyThread` and goes through placement like any other.

A thread moves only while it is not running: at a wake (placement picks a different CPU than `CpuId`), when an idle CPU pulls it out of another queue (`Balance`), or when its affinity changes while it is `Ready`. The mechanism takes both queue locks, removes it from one structure, rewrites `CpuId`, calls `OnThreadMigrate` so the policy rebases its virtual time, and inserts it into the other. A running thread whose affinity no longer allows its CPU is marked for reschedule and its CPU is sent the interrupt; the switch-out re-queue runs placement and moves it then.

`s_tickCount`, `s_switchCount` and `s_nextThreadId` become per-CPU counters summed on read or `Interlocked` increments; `KernelThread.TryStart`, which watches the first two to tell a dead scheduler from a slow one, keeps working on the sums. The debug snapshots refresh from CPU 0's tick only.

`SetScheduler` rebuilds every CPU's structure and allocates while doing it, which it already does under `s_globalLock` today and which the rule above forbids under any lock a tick can spin on. It rendezvous instead: it sends every online CPU a stop interrupt (the same one the collector uses), waits for all of them to park in the handler, performs the swap, and releases them. That is a rare, boot-time or test-time path and the latency is irrelevant.

### 3.4 Affinity

The model is the intersection of what Linux and Windows offer, with their names mapped:

| Concept | Linux | Windows | Proposed |
|---------|-------|---------|----------|
| Hard set: the CPUs a thread may run on | `sched_setaffinity` | `SetThreadAffinityMask` | `SchedulerThread.Affinity`, a `CpuSet` |
| Soft preference | last CPU run on | `SetThreadIdealProcessor` | `SchedulerThread.IdealCpu` |
| Inheritance | the creating thread's set | the process mask | the creating thread's set |
| Default for the main flow | all CPUs, minus `isolcpus` | the process mask | `KernelCpus.DefaultAffinity`, initially `Compute` |
| Reserved CPUs | `isolcpus`, `nohz_full` housekeeping | none | `KernelCpus.Housekeeping`, initially CPU 0 |
| Which CPU am I on | `sched_getcpu` | `GetCurrentProcessorNumber` | `KernelCpus.Current` (`SystemNative_SchedGetCpu` already exists) |
| Interrupt affinity | `/proc/irq/N/smp_affinity` | interrupt policy | later, on `InterruptSource` and the MSI table's `TargetCpu` |

`CpuSet` is a readonly struct of four `ulong`s, 256 CPUs, the width the MADT parser already allows, with `All`, `Single`, `Contains`, `With`, `Without`, `Intersect`, `Count`, `First` and `IsEmpty`. A thread's `Affinity` is never empty; placement intersects it with the online set, and when that intersection is empty (a kernel pinned a thread to a CPU that is not online) placement falls back to the online set and logs it, which is what Linux does when every allowed CPU goes offline.

The housekeeping split is a default, not a mechanism. CPU 0's idle thread is pinned to CPU 0; the driver kit's worker and every driver thread are created from a context on CPU 0 and inherit its set, so they stay there without any code in the kit knowing; every device line and message stays routed to CPU 0 as it is today; the PIT and the alarm thread stay there with them. The main kernel thread gets `DefaultAffinity`, which is every online CPU except the housekeeping ones, or every online CPU when that would leave nothing (one CPU, as today). A kernel that wants its main flow on CPU 0 too sets `KernelCpus.DefaultAffinity = KernelCpus.Online` before creating threads. The reason the split is the default is the one given in the summary: the driver stack excludes its handlers with interrupt masks in dozens of places, which is correct exactly as long as handler and thread share a CPU.

The API lives on the ring, in a new `Cosmos.Kernel.System.Threading` folder. `KernelCpus` exposes `Count`, `Online`, `Housekeeping`, `Compute`, `Current` and `DefaultAffinity`. `ThreadAffinity` exposes `Get(Thread)`, `Set(Thread, CpuSet)`, `SetCurrent(CpuSet)`, `GetIdealCpu`, `SetIdealCpu` and a `Pin(Thread, int cpu)` convenience; a `System.Threading.Thread` is resolved to its `SchedulerThread` through the registry, which keeps a handle to the managed thread already. `KernelThreadInfo` gains `Affinity` and `IdealCpu`. `RhGetProcessCpuCount` reports the online count, so `Environment.ProcessorCount` is right and CoreLib's thread pool grows to the machine, which is also the easiest way to put load on every CPU in a test.

One change belongs with this and is worth its own pull request: the main kernel flow should stop being CPU 0's idle thread. Today `Main` runs on the boot context, so the idle thread blocks, takes mutexes and allocates, and `Mutex` and `InterruptEvent` carry special cases for it. With `__managed__Main` starting a real `KernelThread` of 1 MiB for `Main`, created with `DefaultAffinity`, the boot context becomes a pure halt loop on every CPU, user code never runs on an idle thread, and the main flow lands on the compute CPUs where the default says it should. The one test that depends on the old arrangement, `Mutex_IdleThreadContention_KeepsTicketAccounting`, has to be rethought.

### 3.5 What changes for a policy

The seam is `[Experimental]`, and changing it now, before policies exist outside the tree, is cheaper than later. Two signatures change, `OnThreadMigrate` gains a new caller, and one rule changes.

`SelectCpu(thread, currentCpu, cpuCount)` becomes `SelectCpu(SchedulerThread thread, PerCpuState[] cpus, CpuSet allowed)`: the mechanism computes `allowed` from the thread's affinity and the online set, and verifies the answer, falling back to the first allowed CPU. It is called at the first `ReadyThread` and at every wake. Stride's version keeps its load rule (`TotalTickets` under 80% of the best so far) and adds the two heuristics every scheduler ends up with: a woken thread stays on its last CPU when that CPU is idle or lightly loaded, since its data is in that CPU's caches, and the ideal CPU is tried first when set.

`Balance(state, all)` stops touching other CPUs' queues itself. It returns the thread it wants to pull and the CPU to pull it from; the mechanism performs the move under both locks and calls `OnThreadMigrate`. The mechanism calls it when a CPU is about to run idle (`PickNext` returned null) and periodically from the tick, every hundred ticks or so. Stride's idle-steal of the tail of the longest queue stays, now filtered by the victim's affinity as well as `Pinned`.

Every hook runs under the CPU's queue lock, which the mechanism takes. The four hooks that mask interrupts themselves today (`SetPriority`, `GetPriority` and the two run-queue diagnostics) take `SchedulerManager.LockCpu(state)` instead, a scope that masks and locks. `scheduler-plugging.md` and the `RoundRobinScheduler` test policy follow.

Stride's own statics (`s_onTickLogCount`) move into `StrideCpuData`. Its run queue is already pre-sized to the registry's capacity so that an insertion never allocates in the tick, which is also what the rule in section 3.3 requires of a hook that runs under a queue lock; the same pre-sizing has to hold for any structure a policy adds.

### 3.6 The collector and the allocators

The fast path does not change. A thread's TLAB belongs to the thread, a thread runs on one CPU at a time, and an interrupt handler that allocates uses the interrupted thread's TLAB on the same CPU, so `AllocObject`'s interrupt mask keeps the fast path atomic without a lock. The one shared word on it, `s_totalAllocatedBytes`, goes away: `AllocContext.AllocBytes` already counts per thread, and the total is summed on read.

Everything slower takes one heap lock, an IRQ-safe spinlock: the TLAB refill (free lists, segment bump, new segments), pinned allocation, the GC handle segment fallback, and `Collect` itself, which `AllocObjectSlow` calls while already holding it. Under SMP the 8 KiB TLAB may be worth raising, since every refill is now a lock; that is a measurement, not a decision.

The collection stops the world. The collecting CPU sets a stop flag and sends the stop interrupt (x64 vector `0xF1`, ARM64 SGI 9) to every other online CPU, then waits for each to acknowledge, with a bounded wait that panics naming the CPUs that did not answer. A CPU acknowledges in one of two places. In the stop handler, it stores the interrupt frame's base into its current thread's `StackPointer`, marks the thread as carrying a saved context, acknowledges, and spins inside the handler, interrupts still masked, until the flag clears; the handler's own frame is the thread's `ThreadContext`, so returning is the ordinary `iretq` or `eret` with nothing staged. Or, if the CPU was spinning on a kernel spinlock with interrupts masked when the request came, the spin loop sees the flag: it captures the callee-saved registers and the stack pointer with the helper the precise walk already uses, points the thread's `StackPointer` at that capture, acknowledges, waits for release, and goes back to spinning for its lock. That second case is why no spinlock holder may allocate: a holder that triggers a collection would wait for a CPU that is waiting for it, and the spin-loop safepoint only rescues the waiter, never the holder. With the rule kept, a holder always releases within a bounded number of instructions and then takes the interrupt.

The mark phase then scans the collecting thread precisely, as today, and every other registered thread conservatively from its saved context, whether that context came from a switch, a stop handler or a spin-loop capture; the `Running` skip in `ScanThreadStack` becomes a check for a saved context. `ReturnAllAllocContexts`, `ClearWeakHandles` and the sweep run with the world stopped and need nothing more. The pauses grow with the number of threads and CPUs, as every stop-the-world collector's do; the precise scan of stopped threads (issue [#385](https://github.com/valentinbreiz/nativeaot-patcher/issues/385)) is the path to shorter ones, and parallel marking is a later option once the mark stack is shareable.

The unmanaged heap behind `MemoryOp.Alloc` gets its own IRQ-safe spinlock in `Heap.Alloc` and `Heap.Free`; `PageAllocator` already has one.

### 3.7 Runtime state that is per thread, not global

Two pieces of the runtime hold per-thread state in globals, and both are wrong under preemption already.

`RhpCheckedLockCmpXchg` and `RhpCheckedXchg` become assembly: `lock cmpxchg` and `xchg` on x64, `casal` and `swpal` (or an `ldaxr`/`stlxr` loop on cores without LSE) on ARM64. CoreLib's `Lock`, the thread pool's queues, `LazyInitializer` and every `Interlocked.CompareExchange` on a reference depend on them; the integer forms are already inlined by ILC, which is why `SpinLock` works.

The exception dispatcher's chain head, recursion guard and running-funclet records move onto `SchedulerThread`, and the per-CPU block carries a pointer to the current thread's chain head that the switch path updates, so the assembly stubs reach it in one load through the per-CPU register. A thread that throws, is preempted, and resumes on another CPU then finds its own chain.

Two smaller ones: `CachedInterfaceDispatch.Remember` turns its sequence bump into a compare-exchange from even to odd, so a second writer on another CPU backs off instead of interleaving its stores with the first; and `Serial` takes an IRQ-safe spinlock around each write so two CPUs' lines do not interleave byte by byte, with the scheduler's own log lines gaining a CPU prefix.

### 3.8 Interrupts, timers and devices

Nothing moves in the first version. Every I/O APIC line and every MSI stays aimed at the boot CPU, the PIT and the `TimerManager` callbacks run there, and so do the alarm thread and the driver kit's threads through affinity inheritance. The x64 MSI binder's placeholder (`targetCpu == 0 ? LocalApic.GetId() : (byte)targetCpu`) becomes a lookup in the per-CPU table, which is all it was waiting for, and steering a device's messages to another CPU becomes a later feature on the kit's `InterruptSource`, gated on that driver being reviewed for it. The IPIs use three vectors above the device range (`0xF0` reschedule, `0xF1` stop, `0xF2` reserved for a TLB shootdown and remote calls) and SGIs 8 to 10 on ARM64. The LAPIC stays in xAPIC mode, with the ICR pair written under an interrupt mask; x2APIC, which Limine will enable on request and which turns an IPI into a single `wrmsr`, is a follow-up because it changes every LAPIC register access from MMIO to MSRs. The x64 device mapper's write-combining conversion of a framebuffer block rewrites live page table entries, which under SMP needs a TLB shootdown on the other CPUs; the ARM64 mapper already broadcasts its invalidations (`tlbi vae1is`).

## 4. About "CPU 0 for the GC and the scheduler"

The proposal keeps the segmentation and changes what CPU 0 is segmented for, for three reasons.

A dedicated collector CPU helps only a concurrent collector, and this one is not concurrent: it is non-moving mark-and-sweep with no write barrier (`RhpAssignRef` is a plain store), it scans parked stacks conservatively, and it runs with the world stopped. Running the same stop-the-world collection on CPU 0 instead of on the thread that ran out of memory adds an IPI round trip and changes nothing about the pause. The way to a GC that uses a spare core is first a precise scan of stopped threads (#385), then a barrier, then concurrent marking; each is its own project, and none is on the path to SMP. Lazy or concurrent sweeping is a smaller, realistic step once the heap lock exists, and a housekeeping CPU would be the natural place to run it.

A dedicated scheduling CPU makes every switch slower. If CPU 0 decides what the others run, a thread that blocks on CPU 2 has to tell CPU 0, CPU 0 has to pick a replacement and tell CPU 2, and CPU 2 switches: two IPIs where per-CPU scheduling needs none. On the two to four CPUs the test machines and most real targets have, CPU 0 would also be a quarter to a half of the machine doing bookkeeping.

What the idea is right about is that the kernel's own service work should not compete with the kernel's main flow for a CPU, and that the driver stack should keep the one-CPU invariants it was written under. Both are what the housekeeping default in section 3.4 delivers, with no mechanism beyond affinity and inheritance, and both are undone by a kernel that sets `DefaultAffinity` to every CPU when it wants to.

## 5. Phases

Each phase is a pull request or two, each leaves every existing suite green, and each adds a test that only it can pass. The test runner gains a `cpus` field on profiles and modifiers that both QEMU hosts turn into `-smp`, and an `smp4` modifier that the Threading, GarbageCollector, Timer and HelloWorld suites run under from phase 2 on; today no host passes `-smp`, so every suite runs on one CPU.

| Phase | Work | Proves itself with |
|-------|------|--------------------|
| 0. Correctness on one CPU | Atomic reference exchanges in assembly; exception state per thread; `Interlocked` counters; spinlocks in `Heap` and the handle fallback; the serial lock; the dispatch cache's compare-exchange | The existing suites, and a new `ExceptionHandling_ConcurrentThrows` in Threading: two threads throwing and catching in loops under preemption |
| 1. Identity | The per-CPU block, the register that finds it, staging in the block, the real `GetCurrentCpuId`, the APIC-id and MPIDR tables, handlers passing the index | The existing suites unchanged; `KernelCpus.Current == 0` |
| 2. Bring-up | The Limine MP request, the trampolines, `__managed__ap_main`, `InitializeCurrentCpu` on both controllers, per-CPU timer start, the online mask, per-CPU sleepers, snapshots on CPU 0 only | `Smp_AllCpusOnline`, `Smp_EveryCpuTicks` (per-CPU tick counters advance), and every suite under `smp4` with nothing placed on the application processors |
| 3. Mechanism | Queue and registry locks, the remote wake and its IPI, migration, the policy-swap rendezvous, `CpuSet`, the `ThreadAffinity` and `KernelCpus` API with `DefaultAffinity` still CPU 0 | `Affinity_PinnedThread_RunsOnThatCpu` (`SchedGetCpu` constant), `Affinity_TwoPinnedSpinners_RunAtOnce` (both `Running` in `SchedulerDiagnostics` at the same instant), `Mutex_CrossCpuHandoff`, `InterruptEvent_WakesThreadOnOtherCpu`, `Affinity_ChangeWhileRunning_Migrates` |
| 4. Collector | The heap lock, the stop interrupt, the two acknowledge paths, scanning stopped threads, per-context allocation counters | `Gc_StopTheWorld_AllocationStormOnEveryCpu` (pinned allocators on three CPUs while a fourth forces collections and verifies its graphs), `Gc_Collect_WhileAnotherCpuSpinsOnTheHeapLock` |
| 5. Placement | `SelectCpu` at wake and at first ready, `Balance` when idle and periodically, `DefaultAffinity = Compute`, `Environment.ProcessorCount`, then `Main` on its own thread | `Balance_IdleCpuPullsRunnable`, `Parallel_FourSpinnersFinishInUnderTwiceOneSpinnersTime`, `ThreadPool_WorkersSpreadAcrossCpus`, `DriverKit_ThreadsStayOnHousekeepingCpu` |
| 6. Later | Message steering per CPU, x2APIC, tickless idle, TLB shootdown, lazy sweep, precise scan of stopped threads, parallel mark | |

Phase 0 is worth doing now whatever happens to the rest. Phases 1 and 2 change no observable behavior and can land while the design of 3 to 5 is still being argued. Phase 4 is the hardest and the one to prototype early, together with a one-day spike confirming that Limine starts application processors on the ARM64 `virt` machine under EDK2 as it does on x64.

## 6. Risks and open decisions

The risks, roughly in order of how likely they are to cost a week:

The first is ARM64's memory model. Every flag that crosses CPUs needs its acquire and release, and the places that need one are the places that look fine. The rule is `Volatile` on every cross-CPU field and the spinlock on everything else, and the ARM64 `smp4` cell runs on every pull request so the rare hang shows up in CI rather than on hardware.

The second is the collector's stop protocol. A CPU that masks interrupts and then waits on something other than a kernel spinlock, a device register say, cannot be stopped; the bounded wait and its panic message exist to find such a place quickly, and the kit's rule that a `DeviceLock` holder never waits already forbids the pattern in drivers.

The third is the TSC. `Stopwatch` reads it on x64, and the scheduler's deadlines and Stride's time are in its ticks; KVM and current hardware keep the TSCs in step, older multi-socket machines do not. ARM64's counter is system-wide. A sanity check at bring-up (each application processor compares its TSC with the boot CPU's through the per-CPU block) turns a skewed machine into a log line rather than a mystery.

The fourth is the driver stack under a different default. If a kernel sets `DefaultAffinity` to every CPU and a driver thread it created directly lands on CPU 2 while the device's handler runs on CPU 0, that driver's interrupt-masked sections no longer exclude anything. The housekeeping default avoids it; a kit-level rule that driver threads always inherit the housekeeping set, whatever the default, closes it for the threads the kit creates.

The last is longer collections. Every stopped thread is scanned conservatively from its registers and whole stack, and there are more of them running at once; this is the cost the precise scan (#385) exists to remove.

The decisions that are yours rather than the code's:

1. Whether CPU 0 is housekeeping by default, with `DefaultAffinity` as the override, or whether the default is every CPU. The proposal says housekeeping.
2. Whether `Main` moves off the idle thread onto a real thread. The proposal says yes, in phase 5, as its own pull request.
3. Whether the `SelectCpu` and `Balance` signatures change now, while the seam is experimental. The proposal says yes.
4. Whether a new thread inherits its creator's set (Linux) or the kernel default (Windows). The proposal says the creator's, because it is what keeps the driver kit's threads on CPU 0 without the kit knowing about CPUs.
5. The `CpuSet` width: 256 bits to match the MADT parser, or one `ulong`. The proposal says 256, at 32 bytes per thread.

## 7. Files

| Area | Files |
|------|-------|
| Per-CPU block and identity | `Native.X64/CPU/CpuOps.s` (`wrmsr`, the index helper), `Native.ARM64/CPU/CpuOps.s`, `Native.X64/CPU/Interrupts.s`, `Native.ARM64/CPU/Interrupts.s`, `Native.ARM64/CPU/ContextSwitch.s`, `Core/Bridge/Import/CpuNative.cs`, `Core/Bridge/Import/ContextSwitchNative.cs`, `Core/Scheduler/SchedulerManager.cs` |
| Bring-up | `Boot.Limine/LimineMp.cs` (new), `Boot.Limine/Limine.cs`, a trampoline per native project, `Core/Bridge/Export/SmpNative.cs` (new), `Core/CPU/Smp.cs` (new), `Core/CPU/IInterruptController.cs`, `Core.X64/Cpu/LocalApic.cs`, `Core.X64/Cpu/Idt.cs`, `Core.ARM64/Cpu/GICv3.cs`, `Core.ARM64/Cpu/GICv2.cs`, `HAL/Boot/IPlatformInitializer.cs`, both platform initializers, `Kernel/Internal/Runtime/CompilerHelpers/LibraryInitializer.cs` |
| Mechanism | `Core/Scheduler/SchedulerManager.cs`, `PerCpuState.cs`, `SchedulerThread.cs` (affinity, ideal CPU, saved-context flag, exception state), `SpinLock.cs` (the spin-loop safepoint), `IScheduler.cs`, `Stride/*.cs`, `Core/Bridge/Interop/libSystemNative.cs`, `Core/Scheduler/KernelThread.cs` |
| Affinity API | `System/Threading/CpuSet.cs`, `KernelCpus.cs`, `ThreadAffinity.cs` (new), `System/Diagnostics/KernelThreadInfo.cs`, `Core/Runtime/Stdllib.cs` (`RhGetProcessCpuCount`) |
| Collector | `Core/Memory/GarbageCollector/GarbageCollector.cs`, `.Alloc.cs`, `.Tlab.cs`, `.Mark.cs`, `.PinnedHeap.cs`, `GCHandleManager.cs`, `Core/Memory/Heap/Heap.cs` |
| Runtime | `Core/Runtime/Stdllib.cs` and the two `Runtime.s` (atomics), `Core/Runtime/ExceptionHandling/ExceptionHelper.cs`, `Native.X64/CPU/ExceptionHandling.s`, `Native.ARM64/CPU/ExceptionHandling.s`, `Core/Runtime/CachedInterfaceDispatch.cs`, `Core/IO/Serial.cs` |
| Tests | `tests/profiles.json`, `tests/Cosmos.TestRunner.Engine/TestProfile.cs`, `Hosts/QemuX64Host.cs`, `Hosts/QemuARM64Host.cs`, `tests/Kernels/Cosmos.Kernel.Tests.Threading`, `Cosmos.Kernel.Tests.GarbageCollector`, and a `Cosmos.Kernel.Tests.Smp` suite for the affinity and placement tests |
| Docs | `scheduler.md` (limitations, the tick, GC integration), `sched-concepts/cpu-affinity.md`, `../user/scheduler-plugging.md`, `garbage-collector.md` |
