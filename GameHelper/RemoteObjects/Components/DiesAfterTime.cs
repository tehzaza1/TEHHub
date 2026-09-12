// <copyright file="DiesAfterTime.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.Components
{
    using System;

    /// <summary>
    ///     The <see cref="DiesAfterTime" /> component in the entity.
    /// </summary>
    public class DiesAfterTime : ComponentBase
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="DiesAfterTime" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="DiesAfterTime" /> component.</param>
        public DiesAfterTime(IntPtr address)
            : base(address) { }

        // This marker has no mutable payload. Its presence and owner pointer are fixed for the
        // lifetime of the entity and are populated when the component is first constructed.
        internal override bool RequiresPerFrameRefresh => false;
    }
}
