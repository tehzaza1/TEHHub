// <copyright file="ComponentBase.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>


namespace GameHelper.RemoteObjects.Components
{
    using System;
    using System.Buffers;
    using GameHelper.RemoteEnums;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using GameHelper.Utils;
    using GameOffsets.Objects.Components;
    using GameOffsets.Natives;

    /// <summary>
    ///     Component base object that contains component owner entity address.
    ///     All components in the game have this.
    /// </summary>
    public class ComponentBase : RemoteObjectBase
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="ComponentBase" /> class.
        /// </summary>
        /// <param name="Address"></param>
        public ComponentBase(IntPtr Address) :
            base(Address, true)
        {
        }

        /// <summary>
        ///     Owner entity address of this component.
        /// </summary>
        protected IntPtr OwnerEntityAddress;

        /// <summary>
        ///     Indicates whether the component has data that must be refreshed every entity frame.
        ///     Components whose data is immutable for the lifetime of an entity still receive a
        ///     complete read when their address is first discovered.
        /// </summary>
        internal virtual bool RequiresPerFrameRefresh => true;

        /// <inheritdoc />
        protected override void CleanUpData()
        {
            // Zero the only mutable field in the base. Derived components
            // can override to clean their own state. Previously this threw
            // unconditionally (audit F-112) which propagated through the
            // Address setter and (pre-Phase-1) killed the entity reader.
            this.OwnerEntityAddress = IntPtr.Zero;
        }

        /// <inheritdoc />
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGuiHelper.IntPtrToImGui("Owner Address", this.OwnerEntityAddress);
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var data = Core.Process.Handle.ReadMemory<ComponentHeader>(this.Address);
            this.OwnerEntityAddress = data.EntityPtr;
        }

        /// <summary>
        ///     Validate if the component is pointing to parent entity address or not
        /// </summary>
        /// <param name="parentEntityAddress">true if component is pointing to parent entity address otherwise false</param>
        /// <returns></returns>
        public bool IsParentValid(IntPtr parentEntityAddress)
        {
            return this.OwnerEntityAddress == parentEntityAddress;
        }

        protected void StatUpdator(Dictionary<GameStats, int> stats, StdVector statsptr)
        {
            var elementSize = Unsafe.SizeOf<StatArrayStruct>();
            var byteLength = statsptr.Last.ToInt64() - statsptr.First.ToInt64();
            if (byteLength <= 0 || byteLength % elementSize != 0 || byteLength > 50_000_000)
            {
                lock (stats)
                {
                    stats.Clear();
                }

                return;
            }

            var count = (int)(byteLength / elementSize);
            var buffer = ArrayPool<StatArrayStruct>.Shared.Rent(count);
            try
            {
                if (!Core.Process.Handle.TryReadMemoryArray(statsptr.First, buffer, count, out _))
                {
                    lock (stats)
                    {
                        stats.Clear();
                    }

                    return;
                }

                lock (stats)
                {
                    stats.Clear();
                    for (var i = 0; i < count; i++)
                    {
                        var newStat = buffer[i];
                        stats[(GameStats)newStat.key] = newStat.value;
                    }
                }
            }
            finally
            {
                ArrayPool<StatArrayStruct>.Shared.Return(buffer);
            }
        }

        /// <summary>
        ///     Updates a stat dictionary using a buffer retained by the owning component. Dynamic
        ///     components such as <see cref="Stats"/> call this every entity frame, so retaining
        ///     the small native vector avoids both a new array and shared-pool churn after warm-up.
        /// </summary>
        protected void StatUpdator(
            Dictionary<GameStats, int> stats,
            StdVector statsptr,
            ref StatArrayStruct[] readBuffer)
        {
            var elementSize = Unsafe.SizeOf<StatArrayStruct>();
            var byteLength = statsptr.Last.ToInt64() - statsptr.First.ToInt64();
            if (byteLength <= 0 || byteLength % elementSize != 0 || byteLength > 50_000_000)
            {
                lock (stats)
                {
                    stats.Clear();
                }

                return;
            }

            var count = (int)(byteLength / elementSize);
            if (readBuffer.Length < count)
            {
                readBuffer = new StatArrayStruct[Math.Max(count, readBuffer.Length * 2)];
            }

            if (!Core.Process.Handle.TryReadMemoryArray(statsptr.First, readBuffer, count, out _))
            {
                lock (stats)
                {
                    stats.Clear();
                }

                return;
            }

            lock (stats)
            {
                stats.Clear();
                for (var i = 0; i < count; i++)
                {
                    var newStat = readBuffer[i];
                    stats[(GameStats)newStat.key] = newStat.value;
                }
            }
        }
    }
}
