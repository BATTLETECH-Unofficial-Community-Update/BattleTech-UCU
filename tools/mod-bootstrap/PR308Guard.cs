// UCU guard for MonoMod PR 308, so the ModTek 4.4.x that current mod packs ship works until they move to 4.5. A required
// technical change under mod support.
//
// MonoMod.Core before 1.3.5 (ModTek 4.4.x ships 1.2.2) builds some detours as a 6-byte `jmp [rip+disp32]` through an
// 8-byte cell that must sit within 2 GB of the patched code. To find one, QueryingPagedMemoryAllocator walks pages away
// from the code and asks WindowsSystem's page allocator about each. That allocator reports page 0 as free, and
// VirtualAlloc(NULL, ...) then hands back a page anywhere: the cell is out of reach, the displacement truncates and the
// patched method jumps through garbage (a NullReferenceException loop in a Trampoline or SyncProxy frame). On UCU
// under Proton, Unity 6's Mono leaves no free page near its JIT code, so the walk reaches page 0. That happens in about
// 9 of 10 runs once a few thousand methods are patched, and sometimes during ModTek's own first patches. Retail's Unity
// 2018 layout under Proton leaves room, so this is a UCU regression. MonoMod fixed it in PR 308 (MonoMod.Core 1.3.5,
// carried by ModTek 4.5.1 and later): a page outside the system's application address range is never queryable, so the
// walk stops and MonoMod falls back to its absolute 14-byte detour.
//
// This applies the same rule without patching anything. It wraps the page allocator object inside MonoMod's memory
// allocator (QueryingPagedMemoryAllocator.pageAlloc), so no detour of its own can land out of reach. It is installed
// before the bootstrap's first Harmony.Patch, which runs before ModTek's.
//  - Inactive where MonoMod.Core is 1.3.5 or later, or isn't WindowsSystem's page allocator: nothing is touched.
//  - Not gated on Wine. The rule is upstream's own and is correct on Windows too: Windows never maps those pages, so
//    the only call it changes is the one that would have placed a cell anywhere. Windows can reach that path as well
//    when the 2 GB around the code is full.
// Logs one bootstrap line, pr308|active|... or pr308|inactive|..., then pr308|refused|<address>|<count> at counts 1, 10,
// 100 and so on.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MonoMod.Core.Platforms.Memory;

namespace BattleTechPort
{
    public static class PR308Guard
    {
        static readonly Version Fixed = new Version(1, 3, 5);
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static void Install(Action<string> log)
        {
            try
            {
                Type triple = Type.GetType("MonoMod.Core.Platforms.PlatformTriple, MonoMod.Core", false);
                if (triple == null) { log("pr308|inactive|no MonoMod.Core"); return; }
                Version v = triple.Assembly.GetName().Version;
                if (v >= Fixed) { log("pr308|inactive|MonoMod.Core " + v + " has the fix"); return; }
                object current = triple.GetProperty("Current", Any).GetValue(null, null);
                object system = triple.GetProperty("System", Any).GetValue(current, null);
                object allocator = system.GetType().GetProperty("MemoryAllocator", Any).GetValue(system, null);
                log(Wrap(allocator, system.GetType().FullName, v, log));
            }
            catch (Exception ex)
            {
                log("pr308|FAILED|" + ex);
            }
        }

        // Kept apart so that the MonoMod.Core types resolve only once the version check has passed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string Wrap(object allocator, string systemName, Version v, Action<string> log)
        {
            if (!(allocator is QueryingPagedMemoryAllocator))
                return "pr308|inactive|allocator " + (allocator == null ? "null" : allocator.GetType().FullName);
            FieldInfo field = typeof(QueryingPagedMemoryAllocator).GetField("pageAlloc", Any);
            var inner = field?.GetValue(allocator) as QueryingMemoryPageAllocatorBase;
            if (inner == null || inner.GetType().FullName != "MonoMod.Core.Platforms.Systems.WindowsSystem+PageAllocator")
                return "pr308|inactive|page allocator " + (inner == null ? "not found" : inner.GetType().FullName) + " on " + systemName;
            var guard = new RangeGuard(inner, log);
            field.SetValue(allocator, guard);
            return "pr308|active|MonoMod.Core " + v + "|range " + guard.Min.ToString("x") + ".." + guard.Max.ToString("x");
        }

        sealed class RangeGuard : QueryingMemoryPageAllocatorBase
        {
            [StructLayout(LayoutKind.Sequential)]
            struct SYSTEM_INFO
            {
                public ushort wProcessorArchitecture, wReserved;
                public uint dwPageSize;
                public IntPtr lpMinimumApplicationAddress, lpMaximumApplicationAddress, dwActiveProcessorMask;
                public uint dwNumberOfProcessors, dwProcessorType, dwAllocationGranularity;
                public ushort wProcessorLevel, wProcessorRevision;
            }

            [DllImport("kernel32.dll")] static extern void GetSystemInfo(out SYSTEM_INFO info);

            readonly QueryingMemoryPageAllocatorBase inner;
            readonly Action<string> log;
            internal readonly ulong Min, Max;
            int refused;

            internal RangeGuard(QueryingMemoryPageAllocatorBase inner, Action<string> log)
            {
                this.inner = inner;
                this.log = log;
                GetSystemInfo(out SYSTEM_INFO info);
                Min = (ulong)(long)info.lpMinimumApplicationAddress;
                Max = (ulong)(long)info.lpMaximumApplicationAddress;
            }

            public override uint PageSize => inner.PageSize;
            public override bool TryAllocatePage(IntPtr size, bool executable, out IntPtr allocated) => inner.TryAllocatePage(size, executable, out allocated);
            public override bool TryAllocatePage(IntPtr pageAddr, IntPtr size, bool executable, out IntPtr allocated) => inner.TryAllocatePage(pageAddr, size, executable, out allocated);
            public override bool TryFreePage(IntPtr pageAddr, out string errorMsg) => inner.TryFreePage(pageAddr, out errorMsg);

            public override bool TryQueryPage(IntPtr pageAddr, out bool isFree, out IntPtr allocBase, out IntPtr allocSize)
            {
                ulong a = (ulong)(long)pageAddr;
                if (a >= Min && a <= Max) return inner.TryQueryPage(pageAddr, out isFree, out allocBase, out allocSize);
                isFree = false;
                allocBase = IntPtr.Zero;
                allocSize = IntPtr.Zero;
                int n = Interlocked.Increment(ref refused);
                for (int p = 1; p <= n && p > 0; p *= 10)
                    if (p == n) { try { log("pr308|refused|" + a.ToString("x") + "|" + n); } catch { } break; }
                return false;
            }
        }
    }
}
