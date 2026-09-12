// <copyright file="NPC.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.Components
{
    using System;

    /// <summary>
    ///     The <see cref="NPC" /> component in the entity.
    /// </summary>
    public class NPC : ComponentBase
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="NPC" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="NPC" /> component.</param>
        public NPC(IntPtr address)
            : base(address) { }

        // Entity classification only needs this marker's presence. Re-reading its header every
        // frame cannot change the classification while the component address remains the same.
        internal override bool RequiresPerFrameRefresh => false;
    }
}
