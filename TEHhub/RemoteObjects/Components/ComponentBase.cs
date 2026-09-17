// <copyright file="ComponentBase.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>


namespace TEHhub.RemoteObjects.Components
{
    using System;
    using System.Buffers;
    using TEHhub.RemoteEnums;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using TEHhub.Utils;
    using TEHhub.Offsets.Objects.Components;
    using TEHhub.Offsets.Natives;

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
                        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(
                            stats,
                            (GameStats)newStat.key,
                            out _);
                        value = newStat.value;
                    }
                }
            }
            finally
            {
                ArrayPool<StatArrayStruct>.Shared.Return(buffer);
            }
        }
    }
}
